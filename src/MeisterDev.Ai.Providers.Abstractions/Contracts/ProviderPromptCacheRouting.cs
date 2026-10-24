// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Contracts;

/// <summary>
///     The key that keeps the model calls of one conversation on the provider cache that holds the conversation's
///     earlier turns.
/// </summary>
/// <remarks>
///     <para>
///         A provider that caches a repeated prompt prefix serves it from the cache an earlier call filled. Without a
///         routing hint, a provider spreads calls that begin with the same instructions over its caches, and a later
///         turn of a conversation rarely reaches the cache that holds the turns before it. The provider then charges
///         the full input price for a prefix it has already processed.
///     </para>
///     <para>
///         The host sets the key under <see cref="OptionName" /> in <see cref="ChatOptions.AdditionalProperties" />,
///         and only for a runtime whose driver reports
///         <see cref="ProviderRuntimeCapabilities.SupportsPromptCacheRouting" />. Every call of one conversation
///         carries the same key. The driver sends the key under the vendor's own field name and does not pass the
///         entry on to its vendor client.
///     </para>
/// </remarks>
public static class ProviderPromptCacheRouting
{
    /// <summary>The <see cref="ChatOptions.AdditionalProperties" /> entry that carries the key.</summary>
    public const string OptionName = "meisterdev.promptCacheRoutingKey";

    /// <summary>Reads the key that a call carries.</summary>
    /// <param name="options">The options of the call.</param>
    /// <returns>The key, or <see langword="null" /> when the call carries none.</returns>
    public static string? Read(ChatOptions? options)
    {
        return options?.AdditionalProperties is { } properties
               && properties.TryGetValue(OptionName, out var value)
               && value is string key
               && !string.IsNullOrWhiteSpace(key)
            ? key
            : null;
    }
}
