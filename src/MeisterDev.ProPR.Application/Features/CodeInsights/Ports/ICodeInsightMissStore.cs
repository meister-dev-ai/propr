// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Features.CodeInsights.Ports;

/// <summary>
///     Persistence boundary for the human review threads the reviewer never raised, which is what makes recall
///     measurable rather than assumed.
/// </summary>
/// <remarks>
///     These records are evidence about the reviewer rather than output from it, so they are kept apart from the
///     findings it produced. A harvester needs both boundaries and says so by depending on both.
/// </remarks>
public interface ICodeInsightMissStore
{
    /// <summary>
    ///     Records a harvested human thread, and returns whether this call was the one that recorded it. A
    ///     thread already harvested for this pull request is left alone: a crawl re-observes it on every pass,
    ///     and harvesting it twice would double its contribution to recall.
    /// </summary>
    Task<bool> RecordMissAsync(
        CodeInsightPullRequestKey key,
        CodeInsightMissRecord miss,
        CancellationToken ct = default);

    /// <summary>Returns whether the given human thread has already been harvested for this pull request.</summary>
    Task<bool> HasHarvestedThreadAsync(
        CodeInsightPullRequestKey key,
        string providerThreadId,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns the harvested misses for one pull request (with decrypted discussion), so they are
    ///     inspectable before any dashboard exists. Ordered oldest first.
    /// </summary>
    Task<IReadOnlyList<CodeInsightMissView>> GetMissesForPullRequestAsync(
        CodeInsightPullRequestKey key,
        CancellationToken ct = default);
}
