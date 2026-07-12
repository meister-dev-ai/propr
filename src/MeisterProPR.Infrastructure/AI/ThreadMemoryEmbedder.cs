// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     Infrastructure implementation of <see cref="IThreadMemoryEmbedder" />.
///     Resolves provider-neutral chat and embedding runtimes via <see cref="IAiRuntimeResolver" />.
///     Supports an optional <see cref="IChatClient" /> override for summary-generation tests.
/// </summary>
public sealed partial class ThreadMemoryEmbedder(
    IOptions<AiReviewOptions> options,
    IAiRuntimeResolver aiRuntimeResolver,
    IChatClient? chatClient = null,
    ILogger<ThreadMemoryEmbedder>? logger = null) : IThreadMemoryEmbedder
{
    private const string FallbackSummary = ThreadResolutionSummary.GenerationFailedSummary;

    /// <inheritdoc />
    public async Task<float[]> GenerateEmbeddingAsync(
        string compositeText,
        Guid clientId,
        CancellationToken ct = default)
    {
        var runtime = await aiRuntimeResolver.ResolveEmbeddingRuntimeAsync(
            clientId,
            AiPurpose.EmbeddingDefault,
            options.Value.MemoryEmbeddingDimensions,
            ct);

        var maxInputTokens = runtime.Model.MaxInputTokens ?? 8192;
        var tokenCount = EmbeddingTokenizerRegistry.CountTokens(
            runtime.TokenizerName,
            compositeText);
        if (tokenCount > maxInputTokens)
        {
            throw new InvalidOperationException(
                $"Embedding input exceeds the configured limit of {maxInputTokens} tokens for deployment '{runtime.Model.RemoteModelId}'.");
        }

        var result = await runtime.Generator.GenerateAsync([compositeText], cancellationToken: ct);
        return result[0].Vector.ToArray();
    }

    /// <inheritdoc />
    public async Task<ThreadResolutionSummary> GenerateResolutionSummaryAsync(
        string? filePath,
        string? changeExcerpt,
        string commentHistory,
        Guid clientId,
        CancellationToken ct = default)
    {
        var effectiveChatClient = chatClient;
        var modelId = options.Value.ModelId;

        if (effectiveChatClient is null)
        {
            var runtime = await aiRuntimeResolver.ResolveChatRuntimeAsync(
                clientId,
                AiPurpose.MemoryReconsideration,
                ct);
            modelId = runtime.Model.RemoteModelId;
            effectiveChatClient = runtime.ChatClient;
        }

        try
        {
            var prompt = BuildResolutionSummaryPrompt(filePath, changeExcerpt, commentHistory);
            var messages = new[]
            {
                new ChatMessage(
                    ChatRole.System,
                    """
                    You are a code review analyst. Given a resolved PR review thread, classify how it was resolved and write a concise summary.

                    Respond with ONLY a JSON object, no markdown fences and no surrounding prose, in this exact shape:
                    {"resolution":"<one of: ResolvedByChange, AcceptedWithoutChange, ClosedWithoutResolution, Undetermined>","summary":"<3-5 sentence factual summary>"}

                    Classify "resolution" as:
                    - ResolvedByChange: the thread was resolved by a code change. Name the specific change in the summary if determinable.
                    - AcceptedWithoutChange: the concern was explicitly acknowledged as intentional, by-design, or otherwise accepted without a code change.
                    - ClosedWithoutResolution: the thread was closed with no actual conclusion (for example closed by a reviewer without a fix or a decision, or with no substantive discussion).
                    - Undetermined: you cannot tell from the available context how or whether the thread was actually resolved.

                    Do not invent information not present in the context. If you cannot find a real resolution, use ClosedWithoutResolution or Undetermined rather than guessing one. The "summary" must address: what problem was raised, how it was resolved (or that it was not), which specific change resolved it if determinable, and why the outcome was accepted.
                    """),
                new ChatMessage(ChatRole.User, prompt),
            };

            var response = await effectiveChatClient.GetResponseAsync(
                messages,
                new ChatOptions { ModelId = modelId },
                ct);
            return ParseResolutionResponse(response.Text);
        }
        catch (Exception ex)
        {
            LogSummaryGenerationFailed(logger, ex);
            return new ThreadResolutionSummary(FallbackSummary, ResolutionClarity.Undetermined);
        }
    }

    /// <summary>
    ///     Parses the model's JSON classification response. Any missing, empty, non-string, or
    ///     unparseable field resolves to <see cref="ResolutionClarity.Undetermined" /> so a thread with
    ///     no determinable resolution is never treated as storable. A classification is never paired
    ///     with the placeholder summary, which would otherwise re-inject the very rows the purge removes.
    /// </summary>
    private static ThreadResolutionSummary ParseResolutionResponse(string? responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return Undeterminable();
        }

        var json = ExtractJsonObject(responseText);
        if (json is null)
        {
            return Undeterminable();
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Undeterminable();
            }

            // A classification is only worth storing alongside a real summary. A missing, non-string,
            // or empty summary means the model produced nothing usable, so treat it as undetermined
            // regardless of any classification — never store the placeholder as a genuine resolution.
            if (!root.TryGetProperty("summary", out var summaryElement)
                || summaryElement.ValueKind != JsonValueKind.String)
            {
                return Undeterminable();
            }

            var summary = summaryElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(summary))
            {
                return Undeterminable();
            }

            var clarity = root.TryGetProperty("resolution", out var resolutionElement)
                          && resolutionElement.ValueKind == JsonValueKind.String
                ? MapClarity(resolutionElement.GetString())
                : ResolutionClarity.Undetermined;

            return new ThreadResolutionSummary(summary, clarity);
        }
        catch (JsonException)
        {
            return Undeterminable();
        }
    }

    private static ThreadResolutionSummary Undeterminable() =>
        new(FallbackSummary, ResolutionClarity.Undetermined);

    /// <summary>
    ///     Maps a model-provided resolution token to a <see cref="ResolutionClarity" />, accepting only
    ///     the exact enum names (case-insensitive). Numeric or comma-combined values, which
    ///     <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)" /> would otherwise accept, resolve
    ///     to <see cref="ResolutionClarity.Undetermined" />.
    /// </summary>
    private static ResolutionClarity MapClarity(string? token)
    {
        if (!string.IsNullOrWhiteSpace(token)
            && Enum.TryParse(token, ignoreCase: true, out ResolutionClarity parsed)
            && string.Equals(parsed.ToString(), token.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return parsed;
        }

        return ResolutionClarity.Undetermined;
    }

    /// <summary>
    ///     Extracts the outermost JSON object from a model response, tolerating markdown code fences
    ///     or leading/trailing prose. Returns null when no object-shaped span is present.
    /// </summary>
    private static string? ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private static string BuildResolutionSummaryPrompt(
        string? filePath,
        string? changeExcerpt,
        string commentHistory)
    {
        var parts = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            parts.AppendLine($"File: {filePath}");
        }

        if (!string.IsNullOrWhiteSpace(changeExcerpt))
        {
            parts.AppendLine("Change excerpt:");
            parts.AppendLine(changeExcerpt.Length > 500 ? changeExcerpt[..500] : changeExcerpt);
        }
        else
        {
            parts.AppendLine(
                "Note: No diff excerpt is available. If no code change can be determined from the comments, state explicitly that the resolution did not involve a code change.");
        }

        parts.AppendLine("Comment history:");
        parts.Append(commentHistory);

        return parts.ToString();
    }
}
