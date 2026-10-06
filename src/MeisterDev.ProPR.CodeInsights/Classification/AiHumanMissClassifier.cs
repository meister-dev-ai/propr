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
    IModelUsageRecorder usageRecorder,
    ILogger<AiHumanMissClassifier> logger) : IHumanMissClassifierAttemptReporter
{
    private const int MaxDiscussionChars = 6000;
    private const int MaxRationaleChars = 300;

    /// <inheritdoc />
    public string ClassifierVersion => "human-miss-v1";

    public async Task<HumanMissJudgement?> JudgeAsync(
        HumanMissJudgementRequest request,
        CancellationToken ct = default) =>
        (await this.JudgeWithAttemptAsync(request, ct).ConfigureAwait(false)).Judgement;

    public async Task<HumanMissJudgementResult> JudgeWithAttemptAsync(
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
            return new HumanMissJudgementResult(null, ModelWasAsked: false);
        }
        catch (Exception ex)
        {
            LogResolutionFailed(logger, request.ProviderThreadId, ex);
            return new HumanMissJudgementResult(null, ModelWasAsked: false);
        }

        var modelWasAsked = false;
        try
        {
            var chatClient = runtime.ChatClient ?? throw new InvalidOperationException("The resolved runtime has no chat client.");
            ChatMessage[] messages =
            [
                new(ChatRole.System, BuildSystemPrompt()),
                new(ChatRole.User, BuildUserMessage(request)),
            ];
            var options = new ChatOptions();
            modelWasAsked = true;
            var response = await chatClient.GetResponseAsync(messages, options, ct).ConfigureAwait(false);

            // Recorded before the response is judged usable: the tokens are spent either way.
            await usageRecorder.RecordAsync(request.ClientId, runtime, response, ct).ConfigureAwait(false);

            var judgement = TryParse(response.Text);
            if (judgement is null)
            {
                LogUnusableResponse(logger, request.ProviderThreadId);
            }

            return new HumanMissJudgementResult(judgement, ModelWasAsked: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogCallFailed(logger, request.ProviderThreadId, ex);
            return new HumanMissJudgementResult(null, modelWasAsked);
        }
    }

    private static string BuildSystemPrompt()
    {
        return InsightPrompts.HumanMissSystem();
    }

    private static string BuildUserMessage(HumanMissJudgementRequest request)
    {
        return InsightPrompts.HumanMissUser(
            new InsightPromptModels.HumanMissUserModel(
                request.FilePath ?? "the pull request as a whole",
                request.ThreadResolved ? "resolved" : "still open",
                Truncate(request.Discussion, MaxDiscussionChars)));
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
}
