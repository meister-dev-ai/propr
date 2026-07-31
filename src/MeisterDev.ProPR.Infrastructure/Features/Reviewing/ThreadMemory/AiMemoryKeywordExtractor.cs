// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Text.Json;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.ThreadMemory;

/// <summary>
///     Extracts human-searchable keywords for a resolution memory.
/// </summary>
/// <remarks>
///     Bounded in count and in length per keyword, and filtered to plain word characters. A keyword is shown
///     to an operator, so anything odd that reached one would be a visible leak rather than merely a stored
///     one: the filter is the second line of defence behind only ever reading already-stored summary text.
/// </remarks>
internal sealed partial class AiMemoryKeywordExtractor(
    IAiRuntimeResolver aiRuntimeResolver,
    IModelUsageRecorder usageRecorder,
    ILogger<AiMemoryKeywordExtractor> logger) : IMemoryKeywordExtractor
{
    /// <summary>Most keywords a memory may carry. A long list is not a search aid, it is noise.</summary>
    public const int MaxKeywords = 8;

    /// <summary>Longest a single keyword may be. Anything longer is a phrase or a payload, not a keyword.</summary>
    public const int MaxKeywordLength = 40;

    private const int MaxSummaryChars = 2000;
    private const int MaxExcerptChars = 1500;

    public async Task<IReadOnlyList<string>> ExtractAsync(
        Guid clientId,
        string resolutionSummary,
        string? changeExcerpt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(resolutionSummary))
        {
            return [];
        }

        IResolvedAiChatRuntime runtime;
        try
        {
            runtime = await aiRuntimeResolver
                .ResolveChatRuntimeAsync(clientId, AiPurpose.MemoryReconsideration, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AiPurposeBindingNotConfiguredException ex)
        {
            LogBindingUnavailable(logger, clientId, ex);
            return [];
        }
        catch (Exception ex)
        {
            LogResolutionFailed(logger, clientId, ex);
            return [];
        }

        try
        {
            var response = await runtime.ChatClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, BuildSystemPrompt()),
                    new ChatMessage(ChatRole.User, BuildUserMessage(resolutionSummary, changeExcerpt)),
                ],
                new ChatOptions(),
                ct).ConfigureAwait(false);

            // Recorded before the response is judged usable: the tokens are spent either way.
            await usageRecorder.RecordAsync(clientId, runtime, response, ct).ConfigureAwait(false);

            return Sanitize(TryParse(response.Text));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogCallFailed(logger, clientId, ex);
            return [];
        }
    }

    /// <summary>
    ///     Bounds and cleans a candidate list: trimmed, lower-cased, word characters and internal dashes only,
    ///     de-duplicated, and capped. Anything that does not survive that is dropped rather than shortened,
    ///     because a truncated keyword is worse than a missing one: it looks deliberate.
    /// </summary>
    internal static IReadOnlyList<string> Sanitize(IEnumerable<string> candidates)
    {
        var keywords = new List<string>(MaxKeywords);

        foreach (var candidate in candidates)
        {
            if (keywords.Count == MaxKeywords)
            {
                break;
            }

            var keyword = (candidate ?? string.Empty).Trim().ToLowerInvariant();
            if (keyword.Length == 0 || keyword.Length > MaxKeywordLength)
            {
                continue;
            }

            if (!keyword.All(character => char.IsLetterOrDigit(character) || character is '-' || character is '.'))
            {
                continue;
            }

            if (!keywords.Contains(keyword, StringComparer.Ordinal))
            {
                keywords.Add(keyword);
            }
        }

        return keywords;
    }

    private static string BuildSystemPrompt()
    {
        // The cap the prompt states is the cap Sanitize enforces: one constant, so the model is not asked for a
        // number of keywords that would then be thrown away.
        return ThreadMemoryPrompts.KeywordsSystem(new ThreadMemoryPrompts.KeywordsSystemModel(MaxKeywords));
    }

    private static string BuildUserMessage(string resolutionSummary, string? changeExcerpt)
    {
        var hasExcerpt = !string.IsNullOrWhiteSpace(changeExcerpt);

        return ThreadMemoryPrompts.KeywordsUser(
            new ThreadMemoryPrompts.KeywordsUserModel(
                Truncate(resolutionSummary, MaxSummaryChars),
                hasExcerpt,
                hasExcerpt ? Truncate(changeExcerpt!, MaxExcerptChars) : string.Empty));
    }

    private static IReadOnlyList<string> TryParse(string? responseText)
    {
        var json = ExtractJsonArray(responseText);
        if (json is null)
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return document.RootElement
                .EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => element.GetString() ?? string.Empty)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? ExtractJsonArray(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('[', StringComparison.Ordinal);
        var end = text.LastIndexOf(']');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private static string Truncate(string value, int max)
    {
        return value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "\n…(truncated)");
    }
}
