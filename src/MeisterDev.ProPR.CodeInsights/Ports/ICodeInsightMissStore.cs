using MeisterDev.ProPR.CodeInsights.Contracts;

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.CodeInsights.Ports;

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

    /// <summary>
    ///     Returns whether the stored judgement for this human thread was made against a resolved thread, or
    ///     <see langword="null" /> when the thread has not been harvested for this pull request at all.
    /// </summary>
    /// <remarks>
    ///     The three-way answer is what the harvester needs. Not harvested means judge it; harvested while the
    ///     thread was open means the judgement is provisional and must be replaced once the thread resolves;
    ///     harvested while resolved means it is settled and costs no further model call.
    /// </remarks>
    Task<bool?> GetJudgedThreadResolvedAsync(
        CodeInsightPullRequestKey key,
        string providerThreadId,
        CancellationToken ct = default);

    /// <summary>
    ///     Replaces the stored judgement and discussion for an already-harvested thread, and returns whether a row
    ///     was found to replace. The row keeps its identity, its anchor, and the time it was first harvested, so
    ///     harvest time and judgement time together show that it was revisited.
    /// </summary>
    Task<bool> RejudgeMissAsync(
        CodeInsightPullRequestKey key,
        CodeInsightMissRecord miss,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns the harvested misses for one pull request (with decrypted discussion), so they are
    ///     inspectable before any dashboard exists. Ordered oldest first.
    /// </summary>
    Task<IReadOnlyList<CodeInsightMissView>> GetMissesForPullRequestAsync(
        CodeInsightPullRequestKey key,
        CancellationToken ct = default);
}
