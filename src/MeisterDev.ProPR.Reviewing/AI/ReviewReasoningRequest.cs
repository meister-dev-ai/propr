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
///     Microsoft.Extensions.AI OpenAI adapter for the Responses API builds its request on top of the instance
///     returned by <see cref="ChatOptions.RawRepresentationFactory" /> and leaves a pre-set <c>ReasoningOptions</c>
///     untouched, so this is the mechanism that reaches the wire as <c>reasoning: { … }</c>. The adapter for Chat
///     Completions accepts no Responses options and reads the effort from <see cref="ChatOptions.Reasoning" />. Every
///     request states only the effort <c>none</c> there, for a model that accepts it.
/// </summary>
/// <remarks>
///     That mechanism is per-client by design — the factory is handed the client it is building for — which 
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
    ///     <see cref="ReviewReasoningEffort.None" /> effort sends <c>none</c> to a model that accepts it, on the
    ///     Responses API and on Chat Completions. Any other model receives no effort level and uses its own
    ///     default, because some reasoning models refuse a request that names <c>none</c>. A configured Low, Medium or
    ///     High reaches the Responses API and native protocols. Chat Completions receives no effort for it, because
    ///     OpenAI refuses function tools there with any effort other than <c>none</c> from GPT-5.4 on, and an
    ///     OpenAI-compatible server whose model does not know the field can refuse the request. When neither knob is
    ///     active this is a no-op: byte-identical to sending no reasoning options, and harmless for non-OpenAI clients
    ///     (they ignore <see cref="ChatOptions.RawRepresentationFactory" />).
    /// </summary>
    /// <param name="chatOptions">The options to shape.</param>
    /// <param name="captureReasoning">Whether the model is asked to return a summary of its reasoning.</param>
    /// <param name="reasoningEffort">How hard the model is asked to reason.</param>
    /// <param name="supportsReasoningEffortNone">Whether the bound model accepts the effort <c>none</c>.</param>
    public static ChatOptions ApplyReasoning(
        this ChatOptions chatOptions,
        bool captureReasoning,
        ReviewReasoningEffort reasoningEffort,
        bool supportsReasoningEffortNone = false)
    {
        return Apply(chatOptions, captureReasoning, reasoningEffort, supportsReasoningEffortNone, stateWhenNothingIsAsked: false);
    }

    /// <summary>
    ///     Applies a reasoning decision the control plane resolved for a job, and states it on the request even
    ///     when it asks for nothing.
    /// </summary>
    /// <remarks>
    ///     The options this shapes were built elsewhere: by a runner, or by a pass that started before the
    ///     decision was read. An absent reasoning request there is not the same as a stated refusal, because the
    ///     opt-in those options carried would otherwise stand, so the resolved answer is written out either way.
    ///     A refusal with no effort reaches the provider as the request it would have sent with no reasoning
    ///     options at all. The effort reaches the transports as described on <see cref="ApplyReasoning" />, so Chat
    ///     Completions receives only the effort <c>none</c>, for a model that accepts it.
    /// </remarks>
    /// <param name="chatOptions">The options to shape.</param>
    /// <param name="captureReasoning">Whether this job may keep the model's reasoning.</param>
    /// <param name="reasoningEffort">How hard the model is asked to reason.</param>
    /// <param name="supportsReasoningEffortNone">Whether the bound model accepts the effort <c>none</c>.</param>
    public static ChatOptions ApplyResolvedReasoning(
        this ChatOptions chatOptions,
        bool captureReasoning,
        ReviewReasoningEffort reasoningEffort,
        bool supportsReasoningEffortNone = false)
    {
        return Apply(chatOptions, captureReasoning, reasoningEffort, supportsReasoningEffortNone, stateWhenNothingIsAsked: true);
    }

    /// <summary>Whether these options already carry a reasoning request this shaping produced.</summary>
    /// <remarks>
    ///     Answered from what the factory is bound to, without calling it. A factory a caller supplied builds a
    ///     provider's own options object and may cast the client it is handed to a concrete provider type, so
    ///     calling it to find out what it returns would run caller code against a client it was never given.
    ///     Anything not bound to this shaping was put there by the caller for its own reasons.
    /// </remarks>
    /// <param name="chatOptions">The options to inspect.</param>
    public static bool CarriesAReasoningRequest(this ChatOptions chatOptions)
    {
        return chatOptions.RawRepresentationFactory?.Target is ShapedReasoningRequest;
    }

    private static ChatOptions Apply(
        ChatOptions chatOptions,
        bool captureReasoning,
        ReviewReasoningEffort reasoningEffort,
        bool supportsReasoningEffortNone,
        bool stateWhenNothingIsAsked)
    {
        var effortLevel = MapEffortLevel(reasoningEffort, supportsReasoningEffortNone);

        // Nothing to send: no summary requested and no effort to state. The request stays exactly as it would have
        // been without any reasoning options. This is the path of the effort None on a model that does not accept
        // the effort none, and its request stays byte-identical. A resolved decision is stated even here, because it
        // has to replace whatever the options already carried.
        if (!captureReasoning && effortLevel is null && !stateWhenNothingIsAsked)
        {
            return chatOptions;
        }

        if (reasoningEffort != ReviewReasoningEffort.None)
        {
            // A model asked to reason does not take a sampling temperature, and OpenAI rejects the whole request
            // rather than ignoring the parameter: "Unsupported parameter: 'temperature' is not supported with this
            // model", HTTP 400, on every call. The Anthropic client already drops temperature once a thinking
            // budget is set, for the same reason; doing it here covers the OpenAI adapter, which passes whatever
            // it is given straight through. An effort of None leaves temperature alone, so an ordinary sampling
            // model keeps the configured value.
            chatOptions.Temperature = null;
        }

        // Chat Completions reads the effort from the standard setting, because its adapter accepts no Responses
        // options. Every request states only the effort none there, for a model that accepts it, and leaves a
        // configured Low, Medium or High off Chat Completions for the reasons given on ApplyReasoning. A resolved
        // decision replaces what the options carried there; an ordinary request leaves it as the caller made it.
        if (reasoningEffort == ReviewReasoningEffort.None && supportsReasoningEffortNone)
        {
            chatOptions.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None };
        }
        else if (stateWhenNothingIsAsked)
        {
            chatOptions.Reasoning = null;
        }

        chatOptions.RawRepresentationFactory =
            new ShapedReasoningRequest(captureReasoning, reasoningEffort, effortLevel).Build;

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

    // Maps the configured effort to the Responses effort level, or null when the request carries no effort and the
    // provider keeps its own default.
    private static ResponseReasoningEffortLevel? MapEffortLevel(
        ReviewReasoningEffort reasoningEffort,
        bool supportsReasoningEffortNone)
    {
#pragma warning disable OPENAI001 // Responses reasoning options are an evaluation-stage API surface.
        // The null arm is explicitly typed: ResponseReasoningEffortLevel has an implicit string conversion, so a
        // bare `null` would bind to (ResponseReasoningEffortLevel)(string)null and throw at runtime for None.
        return reasoningEffort switch
        {
            ReviewReasoningEffort.Low => ResponseReasoningEffortLevel.Low,
            ReviewReasoningEffort.Medium => ResponseReasoningEffortLevel.Medium,
            ReviewReasoningEffort.High => ResponseReasoningEffortLevel.High,
            _ => supportsReasoningEffortNone ? ResponseReasoningEffortLevel.None : (ResponseReasoningEffortLevel?)null,
        };
#pragma warning restore OPENAI001
    }

    /// <summary>The reasoning request this shaping puts on a set of options, in the form each family reads.</summary>
    /// <remarks>
    ///     A named type and not a closure, so a factory built here is recognisable by what it is bound to and
    ///     the question "did this shaping put that factory there" is answered without calling it.
    /// </remarks>
    /// <param name="captureReasoning">Whether the model is asked to return its reasoning.</param>
    /// <param name="reasoningEffort">How hard the model is asked to reason.</param>
    /// <param name="effortLevel">
    ///     The effort in the OpenAI library's vocabulary, <c>none</c> for a model told not to reason, or null for
    ///     the model's default.
    /// </param>
#pragma warning disable OPENAI001 // Responses reasoning options are an evaluation-stage API surface.
    private sealed class ShapedReasoningRequest(
        bool captureReasoning,
        ReviewReasoningEffort reasoningEffort,
        ResponseReasoningEffortLevel? effortLevel)
    {
        /// <summary>The request as the client being called understands it.</summary>
        /// <param name="client">The client the options are being built for.</param>
        public object Build(IChatClient client)
        {
            // Native provider protocols map the neutral reasoning request themselves.
            // OpenAI adapters require their provider-specific response options; supplying the neutral
            // request to those adapters would omit the reasoning settings.
            if (client is INativeProtocolChatClient)
            {
                return new ProviderReasoningRequest(MapNeutralEffort(reasoningEffort), captureReasoning);
            }

            var responseOptions = new CreateResponseOptions();

            // A decision that asks for nothing is carried by the absence of the reasoning options: a request
            // that named them empty would be rejected by models that take none.
            if (captureReasoning || effortLevel is not null)
            {
                var reasoningOptions = new ResponseReasoningOptions();

                if (captureReasoning)
                {
                    reasoningOptions.ReasoningSummaryVerbosity = ResponseReasoningSummaryVerbosity.Auto;
                }

                if (effortLevel is { } level)
                {
                    reasoningOptions.ReasoningEffortLevel = level;
                }

                responseOptions.ReasoningOptions = reasoningOptions;
            }

            return responseOptions;
        }
    }
#pragma warning restore OPENAI001
}
