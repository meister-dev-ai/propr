// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Text;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Services;
using MeisterProPR.Domain.ValueObjects;
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
        var state = new EmbeddingBatchState();

        for (var index = 0; index < inputs.Count; index++)
        {
            var input = inputs[index];
            var tokenCount = EmbeddingTokenizerRegistry.CountTokens(deployment.TokenizerName, input);

            if (tokenCount > deployment.MaxInputTokens)
            {
                throw new InvalidOperationException(
                    $"Embedding input exceeds the configured limit of {deployment.MaxInputTokens} tokens for deployment '{deployment.DeploymentName}'.");
            }

            if (state.PendingInputs.Count > 0 && state.PendingTokenCount + tokenCount > deployment.MaxInputTokens)
            {
                await AppendBatchEmbeddingsAsync(embeddingBroker, deployment, clientId, tokenUsageRecorder, usageContext, state, ct);
            }

            state.PendingInputs.Add(input);
            state.PendingContexts.Add(
                usageContext?.InputContexts is not null && usageContext.InputContexts.Count > index
                    ? usageContext.InputContexts[index]
                    : null);
            state.PendingTokenCount += tokenCount;
        }

        await AppendBatchEmbeddingsAsync(embeddingBroker, deployment, clientId, tokenUsageRecorder, usageContext, state, ct);
        return state.Embeddings.AsReadOnly();
    }

    private static async Task AppendBatchEmbeddingsAsync(
        IProCursorEmbeddingBroker embeddingBroker,
        ProCursorEmbeddingDeploymentDto deployment,
        Guid clientId,
        IProCursorTokenUsageRecorder? tokenUsageRecorder,
        ProCursorEmbeddingUsageContext? usageContext,
        EmbeddingBatchState state,
        CancellationToken ct)
    {
        if (state.PendingInputs.Count == 0)
        {
            return;
        }

        var result = await embeddingBroker.GenerateEmbeddingsAsync(
            clientId,
            state.PendingInputs.AsReadOnly(),
            deployment.EmbeddingDimensions,
            ct);
        if (result.Embeddings.Count != state.PendingInputs.Count)
        {
            throw new InvalidOperationException($"Expected {state.PendingInputs.Count} embedding vectors but received {result.Embeddings.Count}.");
        }

        state.Embeddings.AddRange(result.Embeddings);

        if (tokenUsageRecorder is not null && usageContext is not null)
        {
            var capturedUsage = ResolveCapturedUsage(
                result.PromptTokens,
                result.CompletionTokens,
                result.TotalTokens,
                state.PendingTokenCount);
            var firstContext = state.PendingContexts.FirstOrDefault(context => context is not null);
            await tokenUsageRecorder.RecordAsync(
                new ProCursorTokenUsageCaptureRequest(
                    clientId,
                    usageContext.ProCursorSourceId,
                    usageContext.SourceDisplayNameSnapshot,
                    $"{usageContext.RequestIdPrefix}:{usageContext.CallType.ToString().ToLowerInvariant()}:{state.BatchOrdinal}",
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

        state.PendingInputs.Clear();
        state.PendingContexts.Clear();
        state.PendingTokenCount = 0;
        state.BatchOrdinal++;
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
        return AiCostCalculator.Calculate(
                new AiTokenUsage(promptTokens, completionTokens),
                new ModelPricing(
                    capability.InputCostPer1MUsd,
                    capability.OutputCostPer1MUsd,
                    capability.CachedInputCostPer1MUsd))
            .Usd;
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
        var accumulator = new LineChunkAccumulator();

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var lineNumber = chunk.LineStart.HasValue
                ? chunk.LineStart.Value + index
                : (int?)null;

            AccumulateLineForChunkSplit(
                new ChunkSplitInputs(chunk, tokenizerName, maxInputTokens, newlineTokenCount, accumulator, splitChunks, line, lineNumber));
        }

        FlushCurrentChunk(chunk, accumulator, splitChunks);
        return splitChunks.Count == 0 ? [chunk] : splitChunks.AsReadOnly();
    }

    private static void AccumulateLineForChunkSplit(ChunkSplitInputs inputs)
    {
        var lineTokenCount = EmbeddingTokenizerRegistry.CountTokens(inputs.TokenizerName, inputs.Line);

        if (lineTokenCount > inputs.MaxInputTokens)
        {
            FlushCurrentChunk(inputs.Chunk, inputs.Accumulator, inputs.SplitChunks);

            foreach (var segment in SplitOversizedText(inputs.Line, inputs.TokenizerName, inputs.MaxInputTokens))
            {
                inputs.SplitChunks.Add(CreateChunk(inputs.Chunk, inputs.LineNumber, inputs.LineNumber, segment));
            }

            return;
        }

        var additionalTokens = inputs.Accumulator.Lines.Count == 0
            ? lineTokenCount
            : inputs.NewlineTokenCount + lineTokenCount;
        if (inputs.Accumulator.Lines.Count > 0 && inputs.Accumulator.TokenCount + additionalTokens > inputs.MaxInputTokens)
        {
            FlushCurrentChunk(inputs.Chunk, inputs.Accumulator, inputs.SplitChunks);
        }

        if (inputs.Accumulator.Lines.Count == 0)
        {
            inputs.Accumulator.LineStart = inputs.LineNumber;
        }

        inputs.Accumulator.Lines.Add(inputs.Line);
        inputs.Accumulator.TokenCount += inputs.Accumulator.Lines.Count == 1 ? lineTokenCount : additionalTokens;
        inputs.Accumulator.LineEnd = inputs.LineNumber;
    }

    private static void FlushCurrentChunk(
        ProCursorExtractedChunk chunk,
        LineChunkAccumulator accumulator,
        List<ProCursorExtractedChunk> splitChunks)
    {
        if (accumulator.Lines.Count == 0)
        {
            return;
        }

        var content = string.Join('\n', accumulator.Lines).Trim();
        accumulator.Lines.Clear();
        accumulator.TokenCount = 0;
        if (string.IsNullOrWhiteSpace(content))
        {
            accumulator.LineStart = null;
            accumulator.LineEnd = null;
            return;
        }

        splitChunks.Add(CreateChunk(chunk, accumulator.LineStart, accumulator.LineEnd, content));
        accumulator.LineStart = null;
        accumulator.LineEnd = null;
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

    /// <summary>
    ///     Mutable accumulator for the batch currently being assembled, and the embeddings produced by
    ///     every batch flushed so far, for a single embedding-generation call.
    /// </summary>
    private sealed class EmbeddingBatchState
    {
        public List<string> PendingInputs { get; } = [];

        public List<ProCursorTokenUsageInputContext?> PendingContexts { get; } = [];

        public int PendingTokenCount { get; set; }

        public int BatchOrdinal { get; set; }

        public List<float[]> Embeddings { get; } = [];
    }

    /// <summary>
    ///     Mutable accumulator for the run of lines currently being assembled into the next split chunk.
    /// </summary>
    private sealed class LineChunkAccumulator
    {
        public List<string> Lines { get; } = [];

        public int TokenCount { get; set; }

        public int? LineStart { get; set; }

        public int? LineEnd { get; set; }
    }

    private readonly record struct CapturedUsage(
        long PromptTokens,
        long CompletionTokens,
        long TotalTokens,
        bool TokensEstimated);

    private sealed record ChunkSplitInputs(
        ProCursorExtractedChunk Chunk,
        string TokenizerName,
        int MaxInputTokens,
        int NewlineTokenCount,
        LineChunkAccumulator Accumulator,
        List<ProCursorExtractedChunk> SplitChunks,
        string Line,
        int? LineNumber);
}
