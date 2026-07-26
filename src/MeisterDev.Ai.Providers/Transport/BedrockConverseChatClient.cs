// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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
public sealed class BedrockConverseChatClient(IChatClient inner)
    : DelegatingChatClient(inner), INativeProtocolChatClient
{
    /// <inheritdoc />
    public AiProtocolMode NativeProtocol => AiProtocolMode.BedrockConverse;

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
