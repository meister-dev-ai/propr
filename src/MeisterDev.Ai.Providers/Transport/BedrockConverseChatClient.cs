// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Transport;

/// <summary>
///     Wraps the AWS Converse chat client so a request shaped in provider-neutral terms reaches it in the terms
///     it reads.
/// </summary>
/// <remarks>
///     The AWS adapter maps <see cref="ChatOptions.Reasoning" /> onto Bedrock's extended-thinking fields itself,
///     so all this has to do is translate the caller's neutral request into that property — and take our own
///     raw-representation value back off the options, because the adapter expects its own request type there and
///     would be handed something else entirely.
/// </remarks>
/// <param name="inner">The AWS Converse client.</param>
/// <param name="supportsPromptCaching">
///     Whether the model this client addresses can serve part of a prompt from Bedrock's cache. Off unless the
///     host says otherwise, because Bedrock rejects a cache point on a model that does not support one.
/// </param>
public sealed class BedrockConverseChatClient(IChatClient inner, bool supportsPromptCaching = false)
    : DelegatingChatClient(inner), INativeProtocolChatClient
{
    /// <summary>The key the AWS adapter reads a cache point from.</summary>
    private const string CachePointProperty = "CachePoint";

    /// <inheritdoc />
    public AiProtocolMode NativeProtocol => AiProtocolMode.BedrockConverse;

    /// <inheritdoc />
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return base.GetResponseAsync(this.WithCachePoints(messages), this.Translate(options), cancellationToken);
    }

    /// <inheritdoc />
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        return base.GetStreamingResponseAsync(this.WithCachePoints(messages), this.Translate(options), cancellationToken);
    }

    /// <summary>
    ///     Marks the parts of the conversation Bedrock may cache: the system turn, which is identical across the
    ///     files of a review, and the end of the conversation so far, which a follow-up turn repeats.
    /// </summary>
    /// <remarks>
    ///     The marked messages are copies. The same conversation is handed to more than one model in a multi-pass
    ///     review, and a Bedrock-specific property left on it would travel to a provider that has no idea what to
    ///     do with it.
    /// </remarks>
    /// <param name="messages">The conversation about to be sent.</param>
    /// <returns>The conversation, with cache points where they are worth placing.</returns>
    private IReadOnlyList<ChatMessage> WithCachePoints(IEnumerable<ChatMessage> messages)
    {
        var conversation = messages as IList<ChatMessage> ?? messages.ToList();

        if (!supportsPromptCaching || conversation.Count == 0)
        {
            return conversation.AsReadOnly();
        }

        if (!PromptCachePolicy.WorthCaching(PromptCachePolicy.MeasureChars(conversation)))
        {
            return conversation.AsReadOnly();
        }

        var lastSystem = LastIndexOf(conversation, ChatRole.System);
        var marked = new List<ChatMessage>(conversation.Count);

        for (var index = 0; index < conversation.Count; index++)
        {
            var isBreakpoint = index == lastSystem || index == conversation.Count - 1;
            marked.Add(isBreakpoint ? Marked(conversation[index]) : conversation[index]);
        }

        return marked;
    }

    private static int LastIndexOf(IList<ChatMessage> conversation, ChatRole role)
    {
        for (var index = conversation.Count - 1; index >= 0; index--)
        {
            if (conversation[index].Role == role)
            {
                return index;
            }
        }

        return -1;
    }

    private static ChatMessage Marked(ChatMessage message)
    {
        var copy = message.Clone();

        // A fresh dictionary rather than the original's: Clone carries the reference across, so adding to it would
        // reach back into the caller's message and defeat the copy.
        copy.AdditionalProperties = message.AdditionalProperties is null
            ? []
            : new AdditionalPropertiesDictionary(message.AdditionalProperties);

        copy.AdditionalProperties[CachePointProperty] = new CachePointBlock { Type = CachePointType.Default };

        return copy;
    }

    private static ReasoningEffort? MapEffort(ProviderReasoningEffort effort)
    {
        return effort switch
        {
            ProviderReasoningEffort.Low => ReasoningEffort.Low,
            ProviderReasoningEffort.Medium => ReasoningEffort.Medium,
            ProviderReasoningEffort.High => ReasoningEffort.High,
            _ => null,
        };
    }

    private ChatOptions? Translate(ChatOptions? options)
    {
        if (options?.RawRepresentationFactory is null)
        {
            return options;
        }

        var request = options.RawRepresentationFactory(this) as ProviderReasoningRequest;

        // The options are cloned rather than edited: the caller may reuse the same instance for another provider,
        // and a request shaped for this one would then follow it there.
        var translated = options.Clone();
        translated.RawRepresentationFactory = null;

        if (request is not null && MapEffort(request.Effort) is { } effort)
        {
            translated.Reasoning = new ReasoningOptions
            {
                Effort = effort,
                Output = request.CaptureReasoning ? ReasoningOutput.Full : null,
            };
        }

        return translated;
    }
}
