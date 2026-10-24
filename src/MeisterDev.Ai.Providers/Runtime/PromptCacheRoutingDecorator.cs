// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Security.Cryptography;
using System.Text;
using MeisterDev.Ai.Providers.Contracts;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Runtime;

/// <summary>
///     Gives every call of one conversation the same cache routing key, for a runtime whose driver accepts one.
/// </summary>
/// <remarks>
///     <para>
///         This belongs to the runtime pipeline because a conversation is built in several places: the review loop,
///         each verification lookup loop and the synthesis pass assemble their own requests, and a key set at one call
///         site would be missing at the next.
///     </para>
///     <para>
///         The key is derived from the start of the conversation, its first system message and its first user
///         message. Every loop resends that start unchanged on each turn and appends the turns after it, so all calls
///         of one conversation carry the same key and two conversations carry different keys. A conversation that a
///         retry starts again carries the key of its first attempt and reaches the cache that attempt filled. A key
///         the caller set itself is kept.
///     </para>
/// </remarks>
/// <param name="supportsPromptCacheRouting">
///     Whether the driver reported <see cref="ProviderRuntimeCapabilities.SupportsPromptCacheRouting" /> for this
///     runtime. A runtime without it is left undecorated, so its driver never receives the key.
/// </param>
public sealed class PromptCacheRoutingDecorator(bool supportsPromptCacheRouting) : IProviderChatClientDecorator
{
    /// <inheritdoc />
    public ProviderRuntimeStage Stage => ProviderRuntimeStage.Normalization;

    /// <inheritdoc />
    public IChatClient Decorate(IChatClient inner, ProviderEndpoint endpoint, ProviderModelDescriptor model)
    {
        ArgumentNullException.ThrowIfNull(inner);

        return supportsPromptCacheRouting ? new PromptCacheRoutingChatClient(inner) : inner;
    }

    /// <summary>Derives the routing key of the conversation that <paramref name="messages" /> continue.</summary>
    /// <param name="messages">The messages of the call.</param>
    /// <returns>The key, or <see langword="null" /> when the call has neither a system nor a user message.</returns>
    internal static string? ConversationKey(IEnumerable<ChatMessage> messages)
    {
        string? system = null;
        string? user = null;
        foreach (var message in messages)
        {
            if (system is null && message.Role == ChatRole.System)
            {
                system = message.Text;
            }
            else if (user is null && message.Role == ChatRole.User)
            {
                user = message.Text;
            }

            if (system is not null && user is not null)
            {
                break;
            }
        }

        if (system is null && user is null)
        {
            return null;
        }

        // The separator keeps a system message and a user message from hashing like one message split elsewhere.
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{system}\0{user}"));
        return "propr-" + Convert.ToHexStringLower(digest.AsSpan(0, 16));
    }

    private sealed class PromptCacheRoutingChatClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var listed = messages as IList<ChatMessage> ?? [.. messages];
            return base.GetResponseAsync(listed, WithKey(listed, options), cancellationToken);
        }

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var listed = messages as IList<ChatMessage> ?? [.. messages];
            return base.GetStreamingResponseAsync(listed, WithKey(listed, options), cancellationToken);
        }

        // A copy, because callers reuse their options across the turns of a loop and across loops.
        private static ChatOptions? WithKey(IList<ChatMessage> messages, ChatOptions? options)
        {
            if (ProviderPromptCacheRouting.Read(options) is not null || ConversationKey(messages) is not { } key)
            {
                return options;
            }

            var keyed = options?.Clone() ?? new ChatOptions();
            keyed.AdditionalProperties = keyed.AdditionalProperties?.Clone() ?? [];
            keyed.AdditionalProperties[ProviderPromptCacheRouting.OptionName] = key;
            return keyed;
        }
    }
}
