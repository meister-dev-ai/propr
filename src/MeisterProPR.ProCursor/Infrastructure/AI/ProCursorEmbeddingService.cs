// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Text;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.ProCursor.Options;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.AI.ProCursor;

/// <summary>
///     Generates client-scoped ProCursor embeddings using the configured embedding-capable AI connection.
/// </summary>
public sealed class ProCursorEmbeddingService(
    IOptions<ProCursorOptions> options,
    IProCursorEmbeddingBroker embeddingBroker,
    IProCursorTokenUsageRecorder? tokenUsageRecorder = null) : IProCursorEmbeddingService
{
    private readonly ProCursorOptions _options = options.Value;

    /// <inheritdoc />
    public async Task EnsureConfigurationAsync(Guid clientId, CancellationToken ct = default)
    {
        _ = await embeddingBroker.GetDeploymentAsync(clientId, this._options.EmbeddingDimensions, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProCursorExtractedChunk>> NormalizeChunksAsync(
        Guid clientId,
        IReadOnlyList<ProCursorExtractedChunk> chunks,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (chunks.Count == 0)
        {
            return [];
        }

        var deployment = await embeddingBroker.GetDeploymentAsync(clientId, this._options.EmbeddingDimensions, ct);

        var normalizedChunks = new List<ProCursorExtractedChunk>(chunks.Count);
        var nextChunkOrdinalsBySourcePath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var chunk in chunks)
        {
            foreach (var splitChunk in SplitChunkToFitBudget(
                         chunk,
                         deployment.TokenizerName,
                         deployment.MaxInputTokens))
            {
                var nextChunkOrdinal = nextChunkOrdinalsBySourcePath.GetValueOrDefault(splitChunk.SourcePath, 0);
                nextChunkOrdinalsBySourcePath[splitChunk.SourcePath] = nextChunkOrdinal + 1;
                normalizedChunks.Add(splitChunk with { ChunkOrdinal = nextChunkOrdinal });
            }
        }

        return normalizedChunks.AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<float[]>> GenerateEmbeddingsAsync(
        Guid clientId,
        IReadOnlyList<string> inputs,
        ProCursorEmbeddingUsageContext? usageContext = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0)
        {
            return [];
        }

        var deployment = await embeddingBroker.GetDeploymentAsync(clientId, this._options.EmbeddingDimensions, ct);

        var embeddings = new List<float[]>(inputs.Count);
        var currentBatch = new List<string>();
        var currentBatchContexts = new List<ProCursorTokenUsageInputContext?>();
        var currentBatchTokenCount = 0;
        var batchOrdinal = 0;

        for (var index = 0; index < inputs.Count; index++)
        {
            var input = inputs[index];
            var tokenCount = EmbeddingTokenizerRegistry.CountTokens(deployment.TokenizerName, input);

            if (tokenCount > deployment.MaxInputTokens)
            {
                throw new InvalidOperationException(
                    $"Embedding input exceeds the configured limit of {deployment.MaxInputTokens} tokens for deployment '{deployment.DeploymentName}'.");
            }

            if (currentBatch.Count > 0 && currentBatchTokenCount + tokenCount > deployment.MaxInputTokens)
            {
                batchOrdinal = await AppendBatchEmbeddingsAsync(
                    embeddingBroker,
                    deployment,
                    clientId,
                    tokenUsageRecorder,
                    usageContext,
                    currentBatch,
                    currentBatchContexts,
                    currentBatchTokenCount,
                    batchOrdinal,
                    embeddings,
                    ct);
                currentBatchTokenCount = 0;
            }

            currentBatch.Add(input);
            currentBatchContexts.Add(
                usageContext?.InputContexts is not null && usageContext.InputContexts.Count > index
                    ? usageContext.InputContexts[index]
                    : null);
            currentBatchTokenCount += tokenCount;
        }

        _ = await AppendBatchEmbeddingsAsync(
            embeddingBroker,
            deployment,
            clientId,
            tokenUsageRecorder,
            usageContext,
            currentBatch,
            currentBatchContexts,
            currentBatchTokenCount,
            batchOrdinal,
            embeddings,
            ct);
        return embeddings.AsReadOnly();
    }

    private static async Task<int> AppendBatchEmbeddingsAsync(
        IProCursorEmbeddingBroker embeddingBroker,
        ProCursorEmbeddingDeploymentDto deployment,
        Guid clientId,
        IProCursorTokenUsageRecorder? tokenUsageRecorder,
        ProCursorEmbeddingUsageContext? usageContext,
        List<string> batch,
        List<ProCursorTokenUsageInputContext?> batchContexts,
        int promptTokenCount,
        int batchOrdinal,
        List<float[]> embeddings,
        CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            return batchOrdinal;
        }

        var result = await embeddingBroker.GenerateEmbeddingsAsync(
            clientId,
            batch.AsReadOnly(),
            deployment.EmbeddingDimensions,
            ct);
        if (result.Embeddings.Count != batch.Count)
        {
            throw new InvalidOperationException($"Expected {batch.Count} embedding vectors but received {result.Embeddings.Count}.");
        }

        embeddings.AddRange(result.Embeddings);

        if (tokenUsageRecorder is not null && usageContext is not null)
        {
            var capturedUsage = ResolveCapturedUsage(
                result.PromptTokens,
                result.CompletionTokens,
                result.TotalTokens,
                promptTokenCount);
            var firstContext = batchContexts.FirstOrDefault(context => context is not null);
            await tokenUsageRecorder.RecordAsync(
                new ProCursorTokenUsageCaptureRequest(
                    clientId,
                    usageContext.ProCursorSourceId,
                    usageContext.SourceDisplayNameSnapshot,
                    $"{usageContext.RequestIdPrefix}:{usageContext.CallType.ToString().ToLowerInvariant()}:{batchOrdinal}",
                    DateTimeOffset.UtcNow,
                    usageContext.CallType,
                    deployment.DeploymentName,
                    deployment.DeploymentName,
                    deployment.TokenizerName,
                    capturedUsage.PromptTokens,
                    capturedUsage.CompletionTokens,
                    capturedUsage.TotalTokens,
                    capturedUsage.TokensEstimated,
                    CalculateEstimatedCost(
                        capturedUsage.PromptTokens,
                        capturedUsage.CompletionTokens,
                        deployment),
                    true,
                    deployment.AiConnectionId,
                    usageContext.IndexJobId,
                    firstContext?.ResourceId,
                    firstContext?.SourcePath,
                    firstContext?.KnowledgeChunkId),
                ct);
        }

        batch.Clear();
        batchContexts.Clear();
        return batchOrdinal + 1;
    }

    private static CapturedUsage ResolveCapturedUsage(
        long? promptTokens,
        long? completionTokens,
        long? totalTokens,
        int estimatedPromptTokenCount)
    {
        if (!promptTokens.HasValue && !completionTokens.HasValue && !totalTokens.HasValue)
        {
            return new CapturedUsage(estimatedPromptTokenCount, 0, estimatedPromptTokenCount, true);
        }

        var resolvedPromptTokens = promptTokens
                                   ?? totalTokens
                                   ?? estimatedPromptTokenCount;
        var resolvedCompletionTokens = completionTokens ?? 0;
        var resolvedTotalTokens = totalTokens ?? resolvedPromptTokens + resolvedCompletionTokens;

        if (resolvedTotalTokens < resolvedPromptTokens + resolvedCompletionTokens)
        {
            resolvedTotalTokens = resolvedPromptTokens + resolvedCompletionTokens;
        }

        return new CapturedUsage(resolvedPromptTokens, resolvedCompletionTokens, resolvedTotalTokens, false);
    }

    private static decimal? CalculateEstimatedCost(
        long promptTokens,
        long completionTokens,
        ProCursorEmbeddingDeploymentDto capability)
    {
        if (!capability.InputCostPer1MUsd.HasValue && !capability.OutputCostPer1MUsd.HasValue)
        {
            return null;
        }

        var promptCost = capability.InputCostPer1MUsd.GetValueOrDefault() * promptTokens / 1_000_000m;
        var completionCost = capability.OutputCostPer1MUsd.GetValueOrDefault() * completionTokens / 1_000_000m;
        return promptCost + completionCost;
    }

    private static IReadOnlyList<ProCursorExtractedChunk> SplitChunkToFitBudget(
        ProCursorExtractedChunk chunk,
        string tokenizerName,
        int maxInputTokens)
    {
        if (EmbeddingTokenizerRegistry.CountTokens(tokenizerName, chunk.ContentText) <= maxInputTokens)
        {
            return [chunk];
        }

        var normalizedContent = NormalizeLineEndings(chunk.ContentText);
        var lines = normalizedContent.Split('\n');
        var newlineTokenCount = EmbeddingTokenizerRegistry.CountTokens(tokenizerName, "\n");
        var splitChunks = new List<ProCursorExtractedChunk>();
        var currentLines = new List<string>();
        var currentTokenCount = 0;
        int? currentLineStart = null;
        int? currentLineEnd = null;

        void FlushCurrentChunk()
        {
            if (currentLines.Count == 0)
            {
                return;
            }

            var content = string.Join('\n', currentLines).Trim();
            currentLines.Clear();
            currentTokenCount = 0;
            if (string.IsNullOrWhiteSpace(content))
            {
                currentLineStart = null;
                currentLineEnd = null;
                return;
            }

            splitChunks.Add(CreateChunk(chunk, currentLineStart, currentLineEnd, content));
            currentLineStart = null;
            currentLineEnd = null;
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var lineNumber = chunk.LineStart.HasValue
                ? chunk.LineStart.Value + index
                : (int?)null;
            var lineTokenCount = EmbeddingTokenizerRegistry.CountTokens(tokenizerName, line);

            if (lineTokenCount > maxInputTokens)
            {
                FlushCurrentChunk();

                foreach (var segment in SplitOversizedText(line, tokenizerName, maxInputTokens))
                {
                    splitChunks.Add(CreateChunk(chunk, lineNumber, lineNumber, segment));
                }

                continue;
            }

            var additionalTokens = currentLines.Count == 0
                ? lineTokenCount
                : newlineTokenCount + lineTokenCount;
            if (currentLines.Count > 0 && currentTokenCount + additionalTokens > maxInputTokens)
            {
                FlushCurrentChunk();
            }

            if (currentLines.Count == 0)
            {
                currentLineStart = lineNumber;
            }

            currentLines.Add(line);
            currentTokenCount += currentLines.Count == 1 ? lineTokenCount : additionalTokens;
            currentLineEnd = lineNumber;
        }

        FlushCurrentChunk();
        return splitChunks.Count == 0 ? [chunk] : splitChunks.AsReadOnly();
    }

    private static IReadOnlyList<string> SplitOversizedText(string text, string tokenizerName, int maxInputTokens)
    {
        var remaining = text.Trim();
        if (string.IsNullOrWhiteSpace(remaining))
        {
            return [];
        }

        var segments = new List<string>();
        while (!string.IsNullOrWhiteSpace(remaining))
        {
            var prefixLength = FindLargestPrefixWithinBudget(remaining, tokenizerName, maxInputTokens);
            if (prefixLength <= 0)
            {
                throw new InvalidOperationException("Unable to split oversized embedding input into smaller segments.");
            }

            var splitIndex = FindPreferredSplitIndex(remaining, prefixLength);
            if (splitIndex <= 0)
            {
                splitIndex = prefixLength;
            }

            var segment = remaining[..splitIndex].Trim();
            if (string.IsNullOrWhiteSpace(segment))
            {
                splitIndex = prefixLength;
                segment = remaining[..splitIndex].Trim();
            }

            segments.Add(segment);
            remaining = remaining[splitIndex..].TrimStart();
        }

        return segments.AsReadOnly();
    }

    private static int FindLargestPrefixWithinBudget(string text, string tokenizerName, int maxInputTokens)
    {
        var low = 1;
        var high = text.Length;
        var best = 0;

        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            var tokenCount = EmbeddingTokenizerRegistry.CountTokens(tokenizerName, text[..mid]);
            if (tokenCount <= maxInputTokens)
            {
                best = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return best;
    }

    private static int FindPreferredSplitIndex(string text, int maxLength)
    {
        if (maxLength >= text.Length)
        {
            return text.Length;
        }

        var candidate = text.LastIndexOfAny([' ', '\t'], maxLength - 1);
        return candidate > 0 ? candidate : maxLength;
    }

    private static ProCursorExtractedChunk CreateChunk(
        ProCursorExtractedChunk template,
        int? lineStart,
        int? lineEnd,
        string content)
    {
        return new ProCursorExtractedChunk(
            template.SourcePath,
            template.ChunkKind,
            template.Title,
            template.ChunkOrdinal,
            lineStart,
            lineEnd,
            ComputeContentHash(content),
            content);
    }

    private static string NormalizeLineEndings(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    private static string ComputeContentHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private readonly record struct CapturedUsage(
        long PromptTokens,
        long CompletionTokens,
        long TotalTokens,
        bool TokensEstimated);
}
