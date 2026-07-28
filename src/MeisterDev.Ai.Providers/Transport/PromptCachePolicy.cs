// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Transport;

/// <summary>
///     When a prompt is large enough to be worth asking a provider to cache.
/// </summary>
/// <remarks>
///     Shared by the transports that mark cache points explicitly, so they cannot disagree about the floor. A
///     marker is not free: providers charge more for the request that writes the cache, and below a certain size
///     the write costs more than the reads save. Providers also refuse or ignore a marker under their own minimum,
///     which differs per model, so this floor sits above the highest of them rather than tracking each.
/// </remarks>
internal static class PromptCachePolicy
{
    /// <summary>
    ///     The smallest prompt worth marking, in characters. Characters rather than tokens because this only has to
    ///     clear a floor, and tokenising to answer it would cost more than the marker saves. Roughly a thousand
    ///     tokens of English, which clears the per-model minimums in use.
    /// </summary>
    public const int MinimumCacheableChars = 4096;

    /// <summary>Whether a prompt of this size is worth a cache marker.</summary>
    /// <param name="characters">The size of the text about to be sent.</param>
    /// <returns><see langword="true" /> when a marker should be placed.</returns>
    public static bool WorthCaching(long characters)
    {
        return characters >= MinimumCacheableChars;
    }

    /// <summary>Measures the text a conversation carries.</summary>
    /// <param name="messages">The messages about to be sent.</param>
    /// <returns>The total length of their text content.</returns>
    public static long MeasureChars(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        long total = 0;
        foreach (var message in messages)
        {
            foreach (var content in message.Contents.OfType<TextContent>())
            {
                total += content.Text?.Length ?? 0;
            }
        }

        return total;
    }
}
