// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Threads;

/// <summary>
///     Names the state that made a thread pass due, so a pass that already ran for that state is never
///     created a second time and a deterministic failure stops instead of being re-queued every crawl tick.
/// </summary>
public static class ThreadPassTriggerKey
{
    private const int DigestLength = 32;

    /// <summary>
    ///     Builds the key from the two things the trigger compares: the pull request's current revision and
    ///     the non-reviewer comment counts observed on its reviewer-owned threads. A new revision or one more
    ///     comment yields a different key; nothing else does.
    /// </summary>
    /// <param name="revisionKey">The stored revision key the pull request is at.</param>
    /// <param name="observedReplyCountsByThreadId">The observed non-reviewer comment count per thread.</param>
    /// <returns>A stable key for this trigger state.</returns>
    public static string Build(
        string revisionKey,
        IEnumerable<KeyValuePair<string, int>> observedReplyCountsByThreadId)
    {
        ArgumentNullException.ThrowIfNull(observedReplyCountsByThreadId);

        var counts = new StringBuilder();

        // Shorter identifiers first, then ordinal. The ordering has to be total and deterministic for any
        // provider's identifier, and it also has to place a run of decimal identifiers in ascending numeric
        // order: a key already recorded against a completed pass must keep hashing to the same digest, and a
        // plain ordinal sort would put "10" before "9" and re-fire every pass that had already finished.
        foreach (var entry in observedReplyCountsByThreadId
                     .OrderBy(entry => entry.Key.Length)
                     .ThenBy(entry => entry.Key, StringComparer.Ordinal))
        {
            counts.Append(entry.Key)
                .Append(':')
                .Append(entry.Value.ToString(CultureInfo.InvariantCulture))
                .Append(';');
        }

        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(counts.ToString())));
        return $"{revisionKey}|{digest[..DigestLength]}";
    }
}
