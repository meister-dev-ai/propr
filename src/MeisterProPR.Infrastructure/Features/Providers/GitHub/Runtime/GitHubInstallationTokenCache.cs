// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Linq;

namespace MeisterProPR.Infrastructure.Features.Providers.GitHub.Runtime;

internal sealed class GitHubInstallationTokenCache
{
    private const int MaxEntries = 128;
    private static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(5);
    private readonly object _sync = new();
    private readonly Dictionary<string, CachedTokenEntry> _entries = new(StringComparer.Ordinal);

    public bool TryGet(string key, out string token)
    {
        var now = DateTimeOffset.UtcNow;

        lock (this._sync)
        {
            this.RemoveExpiredEntries(now);
            if (this._entries.TryGetValue(key, out var entry))
            {
                this._entries[key] = entry with { LastAccessedAt = now };
                token = entry.Token;
                return true;
            }
        }

        token = string.Empty;
        return false;
    }

    public void Set(string key, string token, DateTimeOffset expiresAt)
    {
        var now = DateTimeOffset.UtcNow;

        lock (this._sync)
        {
            this.RemoveExpiredEntries(now);
            if (!this._entries.ContainsKey(key) && this._entries.Count >= MaxEntries)
            {
                var oldestKey = this._entries
                    .OrderBy(entry => entry.Value.LastAccessedAt)
                    .Select(entry => entry.Key)
                    .FirstOrDefault();

                if (!string.IsNullOrWhiteSpace(oldestKey))
                {
                    this._entries.Remove(oldestKey);
                }
            }

            this._entries[key] = new CachedTokenEntry(token, expiresAt, now);
        }
    }

    private void RemoveExpiredEntries(DateTimeOffset now)
    {
        var expiredKeys = this._entries
            .Where(entry => entry.Value.ExpiresAt <= now.Add(RefreshBuffer))
            .Select(entry => entry.Key)
            .ToArray();

        foreach (var expiredKey in expiredKeys)
        {
            this._entries.Remove(expiredKey);
        }
    }

    private sealed record CachedTokenEntry(string Token, DateTimeOffset ExpiresAt, DateTimeOffset LastAccessedAt);
}
