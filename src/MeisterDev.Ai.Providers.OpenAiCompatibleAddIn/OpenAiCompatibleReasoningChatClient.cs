// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace MeisterDev.Ai.Providers.OpenAiCompatibleAddIn;

/// <summary>
///     Wraps the Microsoft.Extensions.AI adapter over the Chat Completions client of the OpenAI library, so that a
///     reasoning request in provider-neutral terms reaches the request in the terms of Chat Completions.
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
///         only what those options leave unset, so a reasoning effort set here takes precedence over
///         <see cref="ChatOptions.Reasoning" />. For a request that asks for nothing this API is sent, the copy carries
///         no factory, and the adapter reads <see cref="ChatOptions.Reasoning" /> as it does on every other call.
///     </para>
/// </remarks>
/// <param name="inner">The adapter over the Chat Completions client of the OpenAI library.</param>
internal sealed class OpenAiCompatibleReasoningChatClient(IChatClient inner)
    : DelegatingChatClient(inner), INativeProtocolChatClient
{
    /// <summary>The level that asks a model for no reasoning.</summary>
    private const string NoReasoning = "none";

    /// <inheritdoc />
    public string NativeProtocol => OpenAiCompatibleProviderDriver.ChatCompletionsProtocol;

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
    ///     Builds the factory of the Chat Completions options for a neutral request, or returns null when the request
    ///     asks for nothing that this API is sent.
    /// </summary>
    /// <remarks>
    ///     Only the level none reaches Chat Completions. A server whose model does not know the field can refuse the
    ///     request, and from GPT-5.4 on, OpenAI refuses function tools on Chat Completions together with any other
    ///     effort, which a review sends. Chat Completions returns no summary of the reasoning, so a request to capture
    ///     the reasoning adds nothing here.
    /// </remarks>
    /// <param name="request">The neutral request.</param>
    private static Func<IChatClient, object?>? ChatCompletionOptionsFactory(ProviderReasoningRequest request)
    {
        if (!string.Equals(request.Level, NoReasoning, StringComparison.Ordinal))
        {
            return null;
        }

        // The reasoning effort of Chat Completions is marked for evaluation only. This is the one place this add-in
        // uses it, so the diagnostic is suppressed here and not for the whole project.
#pragma warning disable OPENAI001
        return _ => new ChatCompletionOptions { ReasoningEffortLevel = ChatReasoningEffortLevel.None };
#pragma warning restore OPENAI001
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
        translated.RawRepresentationFactory = ChatCompletionOptionsFactory(request);

        // Only the level none reaches Chat Completions, also when the caller states another effort on the shared
        // setting, which the adapter would otherwise send.
        if (translated.Reasoning is { Effort: not ReasoningEffort.None })
        {
            translated.Reasoning = null;
        }

        return translated;
    }
}
