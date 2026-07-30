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
    ILogger<AiDisregardedFindingClassifier> logger) : IDisregardedFindingClassifier
{
    private const int MaxFindingChars = 2000;
    private const int MaxHistoryChars = 6000;
    private const int MaxExcerptChars = 2000;
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
        return "A code reviewer raised a finding on a pull request. The discussion closed without the "
               + "concern being fixed or explicitly accepted. Say why it was turned down.\n\n"
               + "First decide whether it was turned down at all. Set unresolved = true when a person "
               + "engaged with the finding (replied, argued, asked a question) and the thread ended with no "
               + "verdict either way: nobody said it was wrong, nobody accepted it, nothing was changed. That "
               + "is neither a rejection nor an acceptance, and it must not be reported as either. When "
               + "unresolved is true, leave reason null.\n\n"
               + "Otherwise the finding was turned down. Pick exactly one reason:\n"
               + "wrong: the reviewer was mistaken. The finding did not describe a real problem: it misread "
               + "the code, assumed something untrue, or flagged something that is correct as it stands.\n"
               + "out_of_scope: the finding was correct and does not belong to this change. Pre-existing "
               + "code, or work the team tracks elsewhere.\n"
               + "redundant: the finding was correct and something else already covers it. Another tool, "
               + "another finding, or a comment already on the thread.\n"
               + "design_trade_off: the finding was correct and the code is deliberate. A trade-off the team "
               + "made knowingly and would make again.\n"
               + "developer_preference: the finding was correct and the team prefers its own way. Taste "
               + "rather than consequence.\n\n"
               + "Where more than one fits, use the first that applies in the order listed above. A mistaken "
               + "finding is always wrong, whatever else is true of it, and a concrete reason (out of scope, "
               + "already covered) beats a judgement about intent.\n\n"
               + "Judge the finding on its merits against what the discussion reveals. Do NOT infer that a "
               + "finding was wrong merely because nobody acted on it: the whole point of this question is "
               + "that silence and rejection look identical from the outside. When the discussion shows the "
               + "finding was turned down but gives you nothing to say why, set reason to null and keep the "
               + "confidence low rather than guessing a reason.\n\n"
               + "Respond with ONLY a JSON object: {\"wasWrong\":true|false,\"unresolved\":true|false,"
               + "\"reason\":\"wrong\"|\"out_of_scope\"|\"redundant\"|\"design_trade_off\"|"
               + "\"developer_preference\"|null,\"confidence\":0.0,\"rationale\":\"one short sentence\"}";
    }

    private static string BuildUserMessage(DisregardedFindingJudgementRequest request)
    {
        var finding = Truncate(request.FindingMessage, MaxFindingChars);
        var history = Truncate(request.CommentHistory, MaxHistoryChars);
        var location = request.FilePath ?? "the pull request as a whole";

        var message = $"Location: {location}\n\nThe finding:\n{finding}\n\nThe discussion:\n{history}";

        if (!string.IsNullOrWhiteSpace(request.ChangeExcerpt))
        {
            message += $"\n\nRelevant change:\n{Truncate(request.ChangeExcerpt, MaxExcerptChars)}";
        }

        return message;
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

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "No insights-classification model is bound for finding {FindingId}; "
                  + "the wrong-versus-unwanted split is left undecided.")]
    private static partial void LogBindingUnavailable(ILogger logger, Guid findingId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Resolving the insights-classification runtime failed for finding {FindingId}. "
                  + "This is a fault, not a missing binding.")]
    private static partial void LogResolutionFailed(ILogger logger, Guid findingId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The wrong-versus-unwanted judgement call failed for finding {FindingId}.")]
    private static partial void LogCallFailed(ILogger logger, Guid findingId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The wrong-versus-unwanted judgement for finding {FindingId} carried no verdict.")]
    private static partial void LogUnusableResponse(ILogger logger, Guid findingId);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Rejection reason {Reason} contradicts the verdict wasWrong={WasWrong}; "
                  + "the reason is dropped and the verdict kept.")]
    private static partial void LogContradictoryReason(
        ILogger logger,
        CodeInsightRejectionReason reason,
        bool wasWrong);
}
