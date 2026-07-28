// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace MeisterDev.ProPR.Infrastructure.AI;

/// <summary>
///     Configures the outbound reasoning options for a review chat request. Two independent knobs: the reasoning
///     SUMMARY opt-in (so reasoning-capable models return <c>TextReasoningContent</c> the assistant-turn recorder
///     can capture) and the reasoning EFFORT level (how much the model actually reasons). The
///     Microsoft.Extensions.AI OpenAI adapter builds its request on top of the instance returned by
///     <see cref="ChatOptions.RawRepresentationFactory" /> and leaves a pre-set <c>ReasoningOptions</c> untouched, so
///     this is the mechanism that reaches the wire as <c>reasoning: { … }</c>.
/// </summary>
/// <remarks>
///     That mechanism is per-client by design — the factory is handed the client it is building for — which is what
///     lets one call site serve providers that express reasoning incompatibly. A client speaking a provider's own
///     protocol is given the request in neutral terms and maps it itself; only the OpenAI family is handed the
///     OpenAI library's options object, because only it understands one.
/// </remarks>
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

        chatOptions.RawRepresentationFactory = client =>
        {
            // Two arms, and both are load-bearing. A client speaking a provider's own protocol is handed the
            // neutral request and maps it itself. Everything else is an OpenAI-adapter client, and that adapter
            // reads only the OpenAI library's own options object - hand it the neutral form and the reasoning
            // settings are silently dropped, so the OpenAI arm cannot be folded into the neutral one.
            if (client is INativeProtocolChatClient)
            {
                return new ProviderReasoningRequest(MapNeutralEffort(reasoningEffort), captureReasoning);
            }

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

    // Maps the configured effort onto the shared vocabulary a native-protocol driver reads.
    private static ProviderReasoningEffort MapNeutralEffort(ReviewReasoningEffort reasoningEffort)
    {
        return reasoningEffort switch
        {
            ReviewReasoningEffort.Low => ProviderReasoningEffort.Low,
            ReviewReasoningEffort.Medium => ProviderReasoningEffort.Medium,
            ReviewReasoningEffort.High => ProviderReasoningEffort.High,
            _ => ProviderReasoningEffort.None,
        };
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
