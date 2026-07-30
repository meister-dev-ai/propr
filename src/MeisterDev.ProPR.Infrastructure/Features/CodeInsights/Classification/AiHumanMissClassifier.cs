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
///     Judges whether a human-authored review thread describes something an automated reviewer should have
///     caught.
/// </summary>
/// <remarks>
///     <para>
///         This classifier reads human pull-request discussion. That is the same exposure thread-memory
///         already has, and it is a deliberate decision rather than an oversight: recall cannot be measured
///         without reading what humans found. The discussion is stored encrypted at rest and bounded before
///         it is sent.
///     </para>
///     <para>
///         Three judgements come back separately rather than as one verdict, so a later change to where the
///         scope cut-off sits can be re-applied to what is already harvested instead of paying to re-judge
///         every thread.
///     </para>
/// </remarks>
internal sealed partial class AiHumanMissClassifier(
    IAiRuntimeResolver aiRuntimeResolver,
    ILogger<AiHumanMissClassifier> logger) : IHumanMissClassifier
{
    private const int MaxDiscussionChars = 6000;
    private const int MaxRationaleChars = 300;

    /// <inheritdoc />
    public string ClassifierVersion => "human-miss-v1";

    public async Task<HumanMissJudgement?> JudgeAsync(
        HumanMissJudgementRequest request,
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
            LogBindingUnavailable(logger, request.ProviderThreadId, ex);
            return null;
        }
        catch (Exception ex)
        {
            LogResolutionFailed(logger, request.ProviderThreadId, ex);
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

            var judgement = TryParse(response.Text);
            if (judgement is null)
            {
                LogUnusableResponse(logger, request.ProviderThreadId);
            }

            return judgement;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogCallFailed(logger, request.ProviderThreadId, ex);
            return null;
        }
    }

    private static string BuildSystemPrompt()
    {
        return "A human reviewer left a comment thread on a pull request. An automated reviewer looked at the "
               + "same code and said nothing about it. Answer three independent questions about the thread so "
               + "the automated reviewer's recall can be measured honestly.\n\n"
               + "isSubstantive: does the thread raise a real problem with the code? False for questions, "
               + "approvals, praise, process chatter, personal preference, and pure formatting remarks.\n\n"
               + "wasActedOn: was the concern accepted, or did it lead to a change? False when it was argued "
               + "down, dismissed, or simply ignored. A thread marked resolved is evidence but not proof: some "
               + "teams resolve threads as housekeeping.\n\n"
               + "isInScope: is this the kind of issue an automated code reviewer could reasonably be expected "
               + "to catch from the diff and the repository? False when it needs knowledge the code does not "
               + "contain: a product decision, a verbal agreement, an external system's behaviour, an "
               + "organisation-specific convention that is written down nowhere, or deep domain expertise. "
               + "Missing that kind of issue says nothing about review quality.\n\n"
               + "Answer each on its own merits: a thread can easily be substantive and out of scope. "
               + "Respond with ONLY a JSON object: "
               + "{\"isSubstantive\":true|false,\"wasActedOn\":true|false,\"isInScope\":true|false,"
               + "\"confidence\":0.0,\"rationale\":\"one short sentence\"}";
    }

    private static string BuildUserMessage(HumanMissJudgementRequest request)
    {
        var location = request.FilePath ?? "the pull request as a whole";
        var status = request.ThreadResolved ? "resolved" : "still open";

        return $"Location: {location}\nThread status on the provider: {status}\n\nThe discussion:\n"
               + Truncate(request.Discussion, MaxDiscussionChars);
    }

    private static HumanMissJudgement? TryParse(string? responseText)
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
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            // All three are required. Defaulting a missing one either way would decide a recall number on a
            // judgement the model never made, and every default is wrong in one direction or the other.
            if (!TryReadBool(root, "isSubstantive", out var isSubstantive)
                || !TryReadBool(root, "wasActedOn", out var wasActedOn)
                || !TryReadBool(root, "isInScope", out var isInScope))
            {
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

            return new HumanMissJudgement(isSubstantive, wasActedOn, isInScope, confidence, rationale);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadBool(JsonElement root, string name, out bool value)
    {
        if (root.TryGetProperty(name, out var element)
            && element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = element.ValueKind == JsonValueKind.True;
            return true;
        }

        value = false;
        return false;
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
        Message = "No insights-classification model is bound; human thread {ProviderThreadId} is not judged.")]
    private static partial void LogBindingUnavailable(ILogger logger, string providerThreadId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Resolving the insights-classification runtime failed for human thread {ProviderThreadId}. "
                  + "This is a fault, not a missing binding.")]
    private static partial void LogResolutionFailed(ILogger logger, string providerThreadId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The miss judgement call failed for human thread {ProviderThreadId}.")]
    private static partial void LogCallFailed(ILogger logger, string providerThreadId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The miss judgement for human thread {ProviderThreadId} was incomplete; it is not harvested.")]
    private static partial void LogUnusableResponse(ILogger logger, string providerThreadId);
}
