// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Text.Json;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using MeisterDev.ProPR.CodeInsights.Ports;
using MeisterDev.ProPR.CodeInsights.Classification.Prompts;

namespace MeisterDev.ProPR.CodeInsights.Classification;

/// <summary>
///     Judges whether a disregarded finding was wrong or merely unwanted, from the discussion that closed
///     its thread.
/// </summary>
/// <remarks>
///     This distinction cannot come from an SCM thread status: "wrong" and "correct but not worth acting on"
///     both close a thread the same way. Conflating them would put every unwanted-but-correct finding into
///     the false-positive count and make the reviewer look considerably worse than it is, which is the
///     opposite of what a quality measurement is for.
///     Never throws except for cancellation; a null result means the split could not be judged.
/// </remarks>
internal sealed partial class AiDisregardedFindingClassifier(
    IAiRuntimeResolver aiRuntimeResolver,
    IModelUsageRecorder usageRecorder,
    ILogger<AiDisregardedFindingClassifier> logger) : IDisregardedFindingClassifier
{
    /// <summary>
    ///     Ceilings on what one judgement may send and store, so a pathological thread cannot cost many times what
    ///     an ordinary one does. Sized against what the inputs actually are, and generous rather than tight: a
    ///     finding message is a paragraph, a discussion is a handful of replies, an excerpt is a hunk. Most calls
    ///     stay well inside all four, so the values decide only how much the outliers cost, not what the model
    ///     usually sees. The one that changes an answer is the discussion cap, since it truncates the tail of a long
    ///     thread, which is where a verdict is most likely to have been reached; it is the largest of the four for
    ///     that reason. The rationale cap bounds a column, not a prompt: it is one stored sentence.
    /// </summary>
    private const int MaxFindingChars = 2000;

    /// <inheritdoc cref="MaxFindingChars" />
    private const int MaxHistoryChars = 6000;

    /// <inheritdoc cref="MaxFindingChars" />
    private const int MaxExcerptChars = 2000;

    /// <inheritdoc cref="MaxFindingChars" />
    private const int MaxRationaleChars = 300;

    /// <summary>
    ///     The reason vocabulary as the prompt names it, in the precedence the prompt states. Ordered, because a
    ///     rejection can fit more than one reason and an arbitrary pick would make the distribution depend on
    ///     the model's mood rather than on the discussion.
    /// </summary>
    private static readonly (string Token, CodeInsightRejectionReason Reason)[] Reasons =
    [
        ("wrong", CodeInsightRejectionReason.Wrong),
        ("out_of_scope", CodeInsightRejectionReason.OutOfScope),
        ("redundant", CodeInsightRejectionReason.Redundant),
        ("design_trade_off", CodeInsightRejectionReason.DesignTradeOff),
        ("developer_preference", CodeInsightRejectionReason.DeveloperPreference),
    ];

    /// <inheritdoc />
    public string ClassifierVersion => "disregarded-split-v3";

    public async Task<DisregardedFindingJudgement?> JudgeAsync(
        DisregardedFindingJudgementRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        IResolvedAiChatRuntime runtime;
        try
        {
            runtime = await aiRuntimeResolver
                .ResolveChatRuntimeAsync(request.ClientId, AiPurpose.InsightsClassification, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AiPurposeBindingNotConfiguredException ex)
        {
            LogBindingUnavailable(logger, request.FindingId, ex);
            return null;
        }
        catch (Exception ex)
        {
            LogResolutionFailed(logger, request.FindingId, ex);
            return null;
        }

        try
        {
            var response = await runtime.ChatClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, BuildSystemPrompt()),
                    new ChatMessage(ChatRole.User, BuildUserMessage(request)),
                ],
                new ChatOptions(),
                ct).ConfigureAwait(false);

            // Recorded before the response is judged usable: the tokens are spent either way.
            await usageRecorder.RecordAsync(request.ClientId, runtime, response, ct).ConfigureAwait(false);

            var judgement = TryParse(response.Text, logger);
            if (judgement is null)
            {
                LogUnusableResponse(logger, request.FindingId);
            }

            return judgement;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogCallFailed(logger, request.FindingId, ex);
            return null;
        }
    }

    private static string BuildSystemPrompt()
    {
        return InsightPrompts.DisregardedFindingSystem();
    }

    private static string BuildUserMessage(DisregardedFindingJudgementRequest request)
    {
        var hasExcerpt = !string.IsNullOrWhiteSpace(request.ChangeExcerpt);

        return InsightPrompts.DisregardedFindingUser(
            new InsightPromptModels.DisregardedFindingUserModel(
                request.FilePath ?? "the pull request as a whole",
                Truncate(request.FindingMessage, MaxFindingChars),
                Truncate(request.CommentHistory, MaxHistoryChars),
                hasExcerpt,
                hasExcerpt ? Truncate(request.ChangeExcerpt!, MaxExcerptChars) : string.Empty));
    }

    private static DisregardedFindingJudgement? TryParse(string? responseText, ILogger logger)
    {
        var json = ExtractJsonObject(responseText);
        if (json is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("wasWrong", out var wasWrongElement)
                || wasWrongElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                // Without an explicit verdict there is nothing to record. Defaulting either way would put a
                // fabricated judgement into a number that is meant to be evidence.
                return null;
            }

            var confidence = root.TryGetProperty("confidence", out var confidenceElement)
                             && confidenceElement.ValueKind == JsonValueKind.Number
                             && confidenceElement.TryGetDouble(out var parsed)
                ? Math.Clamp(parsed, 0d, 1d)
                : 0d;

            var rationale = root.TryGetProperty("rationale", out var rationaleElement)
                            && rationaleElement.ValueKind == JsonValueKind.String
                ? Truncate(rationaleElement.GetString() ?? string.Empty, MaxRationaleChars)
                : string.Empty;

            var wasWrong = wasWrongElement.ValueKind == JsonValueKind.True;
            var unresolved = root.TryGetProperty("unresolved", out var unresolvedElement)
                             && unresolvedElement.ValueKind == JsonValueKind.True;

            if (unresolved)
            {
                // Neither accepted nor rejected, so there is no rejection to explain and no verdict to record.
                // Carrying either would report a judgement nobody made.
                return new DisregardedFindingJudgement(
                    WasWrong: false,
                    confidence,
                    rationale,
                    Reason: null,
                    IsUnresolved: true);
            }

            var reason = ParseReason(root);

            // The two answers have to agree, and the verdict is the one to trust: it is a narrower question
            // than the reason and the prompt asks for it first. A reason that contradicts it is dropped rather
            // than allowed to move the outcome, which would make a rejection reason able to rewrite precision.
            if (reason is not null && (reason == CodeInsightRejectionReason.Wrong) != wasWrong)
            {
                LogContradictoryReason(logger, reason.Value, wasWrong);
                reason = null;
            }

            return new DisregardedFindingJudgement(wasWrong, confidence, rationale, reason);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Reads the reason token, tolerating the separators a model reaches for on its own (a hyphen or a
    ///     space where the vocabulary uses an underscore). An unrecognised token is no reason rather than a
    ///     guessed one.
    /// </summary>
    private static CodeInsightRejectionReason? ParseReason(JsonElement root)
    {
        if (!root.TryGetProperty("reason", out var element) || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var token = (element.GetString() ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Replace('-', '_')
            .Replace(' ', '_');

        foreach (var (candidate, reason) in Reasons)
        {
            if (string.Equals(token, candidate, StringComparison.Ordinal))
            {
                return reason;
            }
        }

        return null;
    }

    private static string? ExtractJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    private static string Truncate(string value, int max)
    {
        return value.Length <= max ? value : string.Concat(value.AsSpan(0, max), "\n…(truncated)");
    }
}
