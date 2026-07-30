// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Text.Json;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Features.CodeInsights.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Classification;

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
                .ResolveChatRuntimeAsync(clientId, AiPurpose.InsightsClassification, ct)
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
        return "You are given a short summary of how a code-review discussion was resolved. Produce keywords "
               + "an engineer could later type to find this decision again: the concept, the component, the "
               + "technique, the kind of problem. Prefer single words or short hyphenated terms. Avoid generic "
               + "review vocabulary (code, review, comment, change, fix, issue), every memory would match "
               + $"those. At most {MaxKeywords}, lower-case.\n"
               + "Respond with ONLY a JSON array of strings, for example [\"null-check\",\"authentication\"].";
    }

    private static string BuildUserMessage(string resolutionSummary, string? changeExcerpt)
    {
        var message = "Resolution:\n" + Truncate(resolutionSummary, MaxSummaryChars);

        if (!string.IsNullOrWhiteSpace(changeExcerpt))
        {
            message += "\n\nChange:\n" + Truncate(changeExcerpt, MaxExcerptChars);
        }

        return message;
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

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "No insights-classification model is bound for client {ClientId}; memory keywords are skipped.")]
    private static partial void LogBindingUnavailable(ILogger logger, Guid clientId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Resolving the insights-classification runtime failed for client {ClientId} while extracting "
                  + "memory keywords. This is a fault, not a missing binding.")]
    private static partial void LogResolutionFailed(ILogger logger, Guid clientId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Extracting memory keywords failed for client {ClientId}; the memory is stored without them.")]
    private static partial void LogCallFailed(ILogger logger, Guid clientId, Exception ex);
}
