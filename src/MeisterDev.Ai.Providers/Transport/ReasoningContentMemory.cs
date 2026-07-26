// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;

namespace MeisterDev.Ai.Providers.Transport;

/// <summary>
///     Remembers the reasoning text a provider returned with an assistant turn, so the next request can hand it
///     back. Bounded and thread-safe: it lives on a shared HTTP handler, so it must not grow without limit and
///     must tolerate concurrent reviews.
/// </summary>
/// <remarks>
///     Entries are keyed by whatever identifies the assistant turn uniquely — the ids of its tool calls when it
///     made any, otherwise its exact text. Tool-call ids are the important case: the provider only demands the
///     round-trip in a multi-turn exchange with tool calls, and those ids are unique per response, so a key built
///     from them cannot collide across conversations.
/// </remarks>
internal sealed class ReasoningContentMemory(int capacity = 256)
{
    private readonly ConcurrentDictionary<string, string> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _insertionOrder = new();

    /// <summary>Whether anything has been remembered, so a caller can skip work entirely on the common path.</summary>
    public bool IsEmpty => this._entries.IsEmpty;

    /// <summary>Remembers <paramref name="reasoning" /> under <paramref name="key" />, evicting the oldest entry when full.</summary>
    /// <param name="key">The assistant-turn key; ignored when blank.</param>
    /// <param name="reasoning">The reasoning text the provider returned.</param>
    public void Remember(string? key, string reasoning)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        if (this._entries.TryAdd(key, reasoning))
        {
            this._insertionOrder.Enqueue(key);
        }
        else
        {
            this._entries[key] = reasoning;
        }

        // Oldest-first eviction rather than least-recently-used: a conversation's turns are handed back within
        // moments of being produced, so age is a good enough proxy and costs no bookkeeping per read.
        while (this._entries.Count > capacity && this._insertionOrder.TryDequeue(out var oldest))
        {
            this._entries.TryRemove(oldest, out _);
        }
    }

    /// <summary>Returns the reasoning remembered for <paramref name="key" />, or <see langword="null" />.</summary>
    /// <param name="key">The assistant-turn key.</param>
    public string? Recall(string? key)
    {
        return string.IsNullOrEmpty(key) ? null : this._entries.GetValueOrDefault(key);
    }
}
