// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Events;

namespace MeisterProPR.Application.Features.ReviewArchive;

/// <summary>
///     Passive consumer that captures observed pull-request comment threads into the review-archive
///     store. It is a side-channel observer: it never participates in review decisions, deduplication,
///     memory, or the scope snapshot, and only runs when the producing connection opted in to retention.
/// </summary>
public interface IReviewArchiveIngestionService
{
    /// <summary>
    ///     Persists the full thread snapshot carried by <paramref name="evt" /> into the review-archive
    ///     store, touching the parent pull request and upserting the thread with its comments.
    /// </summary>
    Task HandleThreadUpdatedAsync(ThreadUpdatedEvent evt, CancellationToken ct = default);

    /// <summary>
    ///     Persists the per-file unified diffs carried by <paramref name="evt" /> into the review-archive
    ///     store, touching the parent pull request and saving the diffs under the increment's revision key.
    /// </summary>
    Task HandleReviewIncrementDiffsAsync(ReviewIncrementCompletedEvent evt, CancellationToken ct = default);
}
