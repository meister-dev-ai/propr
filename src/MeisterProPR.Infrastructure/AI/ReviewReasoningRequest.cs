// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace MeisterProPR.Infrastructure.AI;

/// <summary>
///     Configures the outbound OpenAI Responses reasoning options for a review chat request. Two independent knobs:
///     the reasoning SUMMARY opt-in (so reasoning-capable models return <c>TextReasoningContent</c> the assistant-turn
///     recorder can capture) and the reasoning EFFORT level (how much the model actually reasons). The
///     Microsoft.Extensions.AI OpenAI adapter builds its request on top of the instance returned by
///     <see cref="ChatOptions.RawRepresentationFactory" /> and leaves a pre-set <c>ReasoningOptions</c> untouched, so
///     this is the mechanism that reaches the wire as <c>reasoning: { … }</c>.
/// </summary>
internal static class ReviewReasoningRequest
{
    /// <summary>
    ///     Applies the reasoning options for a review request. The summary opt-in is governed by
    ///     <paramref name="captureReasoning" /> (asks for <c>summary: "auto"</c> when enabled). The effort level is
    ///     governed by <paramref name="reasoningEffort" /> and applied UNCONDITIONALLY from config — independent of the
    ///     summary opt-in — so a configured effort reaches the wire even when reasoning capture is off. A
    ///     <see cref="ReviewReasoningEffort.None" /> effort leaves the level unset, so the provider keeps its default
    ///     (no reasoning). When neither knob is active this is a no-op: byte-identical to sending no reasoning options,
    ///     and harmless for non-OpenAI clients (they ignore <see cref="ChatOptions.RawRepresentationFactory" />).
    /// </summary>
    public static ChatOptions ApplyReasoning(
        this ChatOptions chatOptions,
        bool captureReasoning,
        ReviewReasoningEffort reasoningEffort)
    {
        var effortLevel = MapEffortLevel(reasoningEffort);

        // Nothing to send: no summary requested and no effort configured. Leave the request exactly as it would have
        // been without any reasoning options — this is the default-none path and keeps current behavior byte-identical.
        if (!captureReasoning && effortLevel is null)
        {
            return chatOptions;
        }

        chatOptions.RawRepresentationFactory = _ =>
        {
#pragma warning disable OPENAI001 // Responses reasoning options are an evaluation-stage API surface.
            var reasoningOptions = new ResponseReasoningOptions();

            if (captureReasoning)
            {
                reasoningOptions.ReasoningSummaryVerbosity = ResponseReasoningSummaryVerbosity.Auto;
            }

            if (effortLevel is { } level)
            {
                reasoningOptions.ReasoningEffortLevel = level;
            }

            return new CreateResponseOptions
            {
                ReasoningOptions = reasoningOptions,
            };
#pragma warning restore OPENAI001
        };

        return chatOptions;
    }

    // Maps the configured effort to the provider effort level, or null for None (the provider keeps its own default).
    private static ResponseReasoningEffortLevel? MapEffortLevel(ReviewReasoningEffort reasoningEffort)
    {
#pragma warning disable OPENAI001 // Responses reasoning options are an evaluation-stage API surface.
        // The null arm is explicitly typed: ResponseReasoningEffortLevel has an implicit string conversion, so a
        // bare `null` would bind to (ResponseReasoningEffortLevel)(string)null and throw at runtime for None.
        return reasoningEffort switch
        {
            ReviewReasoningEffort.Low => ResponseReasoningEffortLevel.Low,
            ReviewReasoningEffort.Medium => ResponseReasoningEffortLevel.Medium,
            ReviewReasoningEffort.High => ResponseReasoningEffortLevel.High,
            _ => (ResponseReasoningEffortLevel?)null,
        };
#pragma warning restore OPENAI001
    }
}
