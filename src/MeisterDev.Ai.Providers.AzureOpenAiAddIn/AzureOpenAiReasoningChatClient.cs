// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using OpenAI.Responses;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace MeisterDev.Ai.Providers.AzureOpenAiAddIn;

/// <summary>
///     Wraps the Microsoft.Extensions.AI adapter over the OpenAI library, so that a reasoning request in
///     provider-neutral terms reaches the API this client calls in the terms of that API.
/// </summary>
/// <remarks>
///     <para>
///         This add-in loads its own copy of the OpenAI library, so OpenAI options that the host builds are a foreign
///         type for the adapter in this add-in, and the adapter ignores them. This client is therefore a
///         native-protocol client: the host hands it the neutral <see cref="ProviderReasoningRequest" />, and the
///         client builds the options of the add-in's own OpenAI library from it.
///     </para>
///     <para>
///         The adapter starts from the options that the factory on <see cref="ChatOptions" /> returns and fills in
///         only what those options leave unset, so reasoning options built here take precedence over
///         <see cref="ChatOptions.Reasoning" />. For a request that asks for nothing, the copy carries no factory, and
///         the adapter reads <see cref="ChatOptions.Reasoning" /> as it does on every other call.
///     </para>
/// </remarks>
/// <param name="inner">The adapter over the client of the OpenAI library.</param>
/// <param name="usesResponsesApi">
///     Whether <paramref name="inner" /> calls the Responses API. Otherwise it calls Chat Completions.
/// </param>
internal sealed class AzureOpenAiReasoningChatClient(IChatClient inner, bool usesResponsesApi)
    : DelegatingChatClient(inner), INativeProtocolChatClient
{
    /// <summary>The level that asks a model for no reasoning.</summary>
    private const string NoReasoning = "none";

    /// <inheritdoc />
    public string NativeProtocol =>
        usesResponsesApi ? AzureOpenAiProviderDriver.ResponsesProtocol : AzureOpenAiProviderDriver.ChatCompletionsProtocol;

    /// <inheritdoc />
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return base.GetResponseAsync(messages, this.Translate(options), cancellationToken);
    }

    /// <inheritdoc />
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return base.GetStreamingResponseAsync(messages, this.Translate(options), cancellationToken);
    }

    /// <summary>
    ///     Builds the factory of the Responses options for a neutral request, or returns null when the request asks
    ///     for nothing.
    /// </summary>
    /// <param name="request">The neutral request.</param>
    /// <param name="shared">The reasoning setting of the options the caller passed.</param>
    private static Func<IChatClient, object?>? ResponsesOptionsFactory(ProviderReasoningRequest request, ReasoningOptions? shared)
    {
        var level = request.Level ?? LevelOf(request.Effort);
        if (level is null && !request.CaptureReasoning)
        {
            return null;
        }

        // Reasoning options built here hide ChatOptions.Reasoning from the adapter. When the request names no level,
        // the effort of that setting is copied, because the host states the effort none there for a model that
        // accepts it.
        level ??= LevelOf(shared?.Effort);
        var captureReasoning = request.CaptureReasoning;

        // A new instance on every call, because the adapter writes the rest of the request into the options it is
        // given.
        return _ =>
        {
            var reasoning = new ResponseReasoningOptions();
            if (level is not null)
            {
                reasoning.ReasoningEffortLevel = new ResponseReasoningEffortLevel(level);
            }

            if (captureReasoning)
            {
                reasoning.ReasoningSummaryVerbosity = ResponseReasoningSummaryVerbosity.Auto;
            }

            return new CreateResponseOptions { ReasoningOptions = reasoning };
        };
    }

    /// <summary>
    ///     Builds the factory of the Chat Completions options for a neutral request, or returns null when the request
    ///     asks for nothing that this API is sent.
    /// </summary>
    /// <remarks>
    ///     Only the level none reaches Chat Completions. From GPT-5.4 on, the OpenAI models that an Azure resource
    ///     deploys refuse function tools on Chat Completions together with any other effort, and a review sends
    ///     function tools. Chat Completions returns no summary of the reasoning, so a request to capture the reasoning
    ///     adds nothing here.
    /// </remarks>
    /// <param name="request">The neutral request.</param>
    private static Func<IChatClient, object?>? ChatCompletionOptionsFactory(ProviderReasoningRequest request)
    {
        if (!string.Equals(request.Level, NoReasoning, StringComparison.Ordinal))
        {
            return null;
        }

        return _ => new ChatCompletionOptions { ReasoningEffortLevel = ChatReasoningEffortLevel.None };
    }

    /// <summary>The level a neutral effort names, or null for the effort None, which names no level.</summary>
    /// <param name="effort">The neutral effort.</param>
    private static string? LevelOf(ProviderReasoningEffort effort)
    {
        return effort switch
        {
            ProviderReasoningEffort.Low => "low",
            ProviderReasoningEffort.Medium => "medium",
            ProviderReasoningEffort.High => "high",
            _ => null,
        };
    }

    /// <summary>The level the reasoning setting of the options states, or null when it states none.</summary>
    /// <param name="effort">The effort of that setting.</param>
    private static string? LevelOf(ReasoningEffort? effort)
    {
        return effort switch
        {
            ReasoningEffort.None => NoReasoning,
            ReasoningEffort.Low => "low",
            ReasoningEffort.Medium => "medium",
            ReasoningEffort.High => "high",
            ReasoningEffort.ExtraHigh => "xhigh",
            _ => null,
        };
    }

    /// <summary>
    ///     The options to hand the adapter: for a neutral reasoning request, a copy whose factory builds the options of
    ///     this add-in's OpenAI library, and otherwise the options unchanged.
    /// </summary>
    /// <param name="options">The options the caller passed.</param>
    private ChatOptions? Translate(ChatOptions? options)
    {
        if (options?.RawRepresentationFactory?.Invoke(this) is not ProviderReasoningRequest request)
        {
            return options;
        }

        // A copy, because the caller may send the same instance to another client, and a factory replaced on it would
        // reach that client too.
        var translated = options.Clone();
        translated.RawRepresentationFactory = usesResponsesApi
            ? ResponsesOptionsFactory(request, options.Reasoning)
            : ChatCompletionOptionsFactory(request);

        // Only the level none reaches Chat Completions, also when the caller states another effort on the shared
        // setting, which the adapter would otherwise send.
        if (!usesResponsesApi && translated.Reasoning is { Effort: not ReasoningEffort.None })
        {
            translated.Reasoning = null;
        }

        return translated;
    }
}
