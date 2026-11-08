// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.AI;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace MeisterDev.ProPR.Infrastructure.AI;

/// <summary>
///     Configures the outbound reasoning options for a review chat request. Two independent knobs: the reasoning
///     SUMMARY opt-in (so reasoning-capable models return <c>TextReasoningContent</c> the assistant-turn recorder
///     can capture) and the reasoning LEVEL (how much the model reasons, by the provider's name). The
///     Microsoft.Extensions.AI OpenAI adapter for the Responses API builds its request on top of the instance
///     returned by <see cref="ChatOptions.RawRepresentationFactory" /> and leaves a pre-set <c>ReasoningOptions</c>
///     untouched, so this is the mechanism that reaches the wire as <c>reasoning: { … }</c>. The adapter for Chat
///     Completions accepts no Responses options and reads the effort from <see cref="ChatOptions.Reasoning" />. Every
///     request states only the level <c>none</c> there.
/// </summary>
/// <remarks>
///     <para>
///         That mechanism is per-client by design — the factory is handed the client it is building for — which lets
///         one call site serve providers that express reasoning incompatibly. A client speaking a provider's own
///         protocol, which includes every provider add-in that implements <see cref="INativeProtocolChatClient" />, is
///         given the request in neutral terms with the level by name and maps it itself; only a client of the host's
///         own OpenAI library is handed that library's options object. A provider add-in loads its own copy of the
///         OpenAI library, so for an add-in that object is a foreign type, and the add-in drops it. On the Responses API
///         the level is therefore also stated in <see cref="ChatOptions.Reasoning" />, which lives in the abstractions
///         the host shares with every add-in, as far as that shared setting has a value for it.
///     </para>
///     <para>
///         A model receives only a level it accepts, see <see cref="ReasoningLevelSelection.Sendable" />.
///     </para>
/// </remarks>
internal static class ReviewReasoningRequest
{
    /// <summary>The level that asks a model for no reasoning.</summary>
    public const string NoneLevel = "none";

    /// <summary>
    ///     Applies the reasoning options for a review request. The summary opt-in is governed by
    ///     <paramref name="captureReasoning" /> (asks for <c>summary: "auto"</c> when enabled). The level is governed by
    ///     <paramref name="reasoningLevel" /> and applied independent of the summary opt-in, so a configured level
    ///     reaches the wire even when reasoning capture is off. The level <c>none</c> reaches the Responses API, Chat
    ///     Completions and native protocols. Any other level reaches the Responses API and native protocols; Chat
    ///     Completions receives no other level, because OpenAI refuses function tools there with any effort other than
    ///     <c>none</c> from GPT-5.4 on, and an OpenAI-compatible server whose model does not know the field can refuse
    ///     the request. When neither knob is active this is a no-op: byte-identical to sending no reasoning options.
    /// </summary>
    /// <param name="chatOptions">The options to shape.</param>
    /// <param name="captureReasoning">Whether the model is asked to return a summary of its reasoning.</param>
    /// <param name="reasoningLevel">The level the places chose, or null for no level.</param>
    /// <param name="acceptedLevels">The levels the bound model accepts, or null when no source knows them.</param>
    /// <param name="usesResponsesApi">Whether the bound model is called through the Responses API.</param>
    public static ChatOptions ApplyReasoning(
        this ChatOptions chatOptions,
        bool captureReasoning,
        string? reasoningLevel,
        IReadOnlyList<string>? acceptedLevels = null,
        bool usesResponsesApi = false)
    {
        return Apply(chatOptions, captureReasoning, reasoningLevel, acceptedLevels, usesResponsesApi, stateWhenNothingIsAsked: false);
    }

    /// <summary>
    ///     Applies a reasoning decision the control plane resolved for a job, and states it on the request even
    ///     when it asks for nothing.
    /// </summary>
    /// <remarks>
    ///     The options this shapes were built elsewhere: by a runner, or by a pass that started before the
    ///     decision was read. An absent reasoning request there is not the same as a stated refusal, because the
    ///     opt-in those options carried would otherwise stand, so the resolved answer is written out either way.
    ///     A refusal with no level reaches the provider as the request it would have sent with no reasoning
    ///     options at all. The level reaches the transports as described on <see cref="ApplyReasoning" />.
    /// </remarks>
    /// <param name="chatOptions">The options to shape.</param>
    /// <param name="captureReasoning">Whether this job may keep the model's reasoning.</param>
    /// <param name="reasoningLevel">The level the places chose, or null for no level.</param>
    /// <param name="acceptedLevels">The levels the bound model accepts, or null when no source knows them.</param>
    /// <param name="usesResponsesApi">Whether the bound model is called through the Responses API.</param>
    public static ChatOptions ApplyResolvedReasoning(
        this ChatOptions chatOptions,
        bool captureReasoning,
        string? reasoningLevel,
        IReadOnlyList<string>? acceptedLevels = null,
        bool usesResponsesApi = false)
    {
        return Apply(chatOptions, captureReasoning, reasoningLevel, acceptedLevels, usesResponsesApi, stateWhenNothingIsAsked: true);
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

    /// <summary>The level a reasoning request this shaping produced names, or null when it names none.</summary>
    /// <param name="chatOptions">Options that carry a reasoning request of this shaping.</param>
    public static string? ShapedReasoningLevel(this ChatOptions chatOptions)
    {
        return (chatOptions.RawRepresentationFactory?.Target as ShapedReasoningRequest)?.Level;
    }

    private static ChatOptions Apply(
        ChatOptions chatOptions,
        bool captureReasoning,
        string? reasoningLevel,
        IReadOnlyList<string>? acceptedLevels,
        bool usesResponsesApi,
        bool stateWhenNothingIsAsked)
    {
        var level = ReasoningLevelSelection.Sendable(ReasoningLevelSelection.Normalize(reasoningLevel), acceptedLevels);

        // Nothing to send: no summary requested and no level to state. The request stays exactly as it would have
        // been without any reasoning options. A resolved decision is stated even here, because it has to replace
        // whatever the options already carried.
        if (!captureReasoning && level is null && !stateWhenNothingIsAsked)
        {
            return chatOptions;
        }

        if (level is not null && level != NoneLevel)
        {
            // A model asked to reason does not take a sampling temperature, and OpenAI rejects the whole request
            // rather than ignoring the parameter: "Unsupported parameter: 'temperature' is not supported with this
            // model", HTTP 400, on every call. The Anthropic client already drops temperature once thinking is
            // requested, for the same reason; doing it here covers the OpenAI adapter, which passes whatever it is
            // given straight through. The level none and no level leave temperature alone, so an ordinary sampling
            // model keeps the configured value.
            chatOptions.Temperature = null;
        }

        // Chat Completions reads the effort from the shared setting, because its adapter accepts no Responses
        // options. A request states the level none there, and leaves every other level off Chat Completions for the
        // reasons given on ApplyReasoning. A model called through the Responses API receives every other level in the
        // shared setting as well, as far as the setting has a value for it. A resolved decision replaces what the
        // options carried there; an ordinary request leaves it as the caller made it.
        if (level == NoneLevel)
        {
            chatOptions.Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None };
        }
        else if (usesResponsesApi && level is not null && SharedEffort(level) is { } shared)
        {
            // The Responses options the factory below builds are a foreign type for a provider add-in, which loads its
            // own copy of the OpenAI library and drops them. The shared setting crosses that boundary, and the add-in's
            // Responses adapter writes it into the request. An add-in that reads the level by name from the neutral
            // request replaces this value with the name.
            chatOptions.Reasoning = new ReasoningOptions { Effort = shared };
        }
        else if (stateWhenNothingIsAsked)
        {
            chatOptions.Reasoning = null;
        }

        chatOptions.RawRepresentationFactory = new ShapedReasoningRequest(captureReasoning, level).Build;
        return chatOptions;
    }

    // The value of the setting every provider add-in shares with the host that comes nearest to a level. The setting
    // has no value for minimal and max, so minimal travels as low and max as the largest value; an add-in that reads
    // the name sends minimal and max themselves. A name off the shared order has no nearest value.
    private static ReasoningEffort? SharedEffort(string level)
    {
        return level switch
        {
            "minimal" or "low" => ReasoningEffort.Low,
            "medium" => ReasoningEffort.Medium,
            "high" => ReasoningEffort.High,
            "xhigh" or "max" => ReasoningEffort.ExtraHigh,
            _ => null,
        };
    }

    // The neutral value a family reads when it does not read the level by name, as the contract describes it.
    private static ProviderReasoningEffort NeutralEffort(string? level)
    {
        return level switch
        {
            "minimal" or "low" => ProviderReasoningEffort.Low,
            "medium" => ProviderReasoningEffort.Medium,
            null or NoneLevel => ProviderReasoningEffort.None,
            _ => ProviderReasoningEffort.High,
        };
    }

    /// <summary>The reasoning request this shaping puts on a set of options, in the form each family reads.</summary>
    /// <remarks>
    ///     A named type and not a closure, so a factory built here is recognisable by what it is bound to and
    ///     the question "did this shaping put that factory there" is answered without calling it.
    /// </remarks>
    /// <param name="captureReasoning">Whether the model is asked to return its reasoning.</param>
    /// <param name="level">The level the model receives, or null for the model's default.</param>
#pragma warning disable OPENAI001 // Responses reasoning options are an evaluation-stage API surface.
    private sealed class ShapedReasoningRequest(bool captureReasoning, string? level)
    {
        /// <summary>The level the request names, or null.</summary>
        public string? Level => level;

        /// <summary>The request as the client being called understands it.</summary>
        /// <param name="client">The client the options are being built for.</param>
        public object Build(IChatClient client)
        {
            // Native provider protocols and the provider add-ins map the neutral request themselves. An add-in that
            // serves Chat Completions sends only the level none there, for the reasons given on ApplyReasoning.
            if (client is INativeProtocolChatClient)
            {
                return new ProviderReasoningRequest(NeutralEffort(level), captureReasoning) { Level = level };
            }

            // The host's own Responses options are read only by a Responses client; the Chat Completions adapter reads
            // the shared setting.
            var responseOptions = new CreateResponseOptions();

            // A decision that asks for nothing is carried by the absence of the reasoning options: a request
            // that named them empty would be rejected by models that take none.
            if (captureReasoning || level is not null)
            {
                var reasoningOptions = new ResponseReasoningOptions();
                if (captureReasoning)
                {
                    reasoningOptions.ReasoningSummaryVerbosity = ResponseReasoningSummaryVerbosity.Auto;
                }

                if (level is not null)
                {
                    reasoningOptions.ReasoningEffortLevel = new ResponseReasoningEffortLevel(level);
                }

                responseOptions.ReasoningOptions = reasoningOptions;
            }

            return responseOptions;
        }
    }
#pragma warning restore OPENAI001
}
