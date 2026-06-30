// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.ReviewArchive;
using MeisterProPR.Domain.Events;

namespace MeisterProPR.Infrastructure.Features.ReviewArchive.Persistence;

/// <summary>
///     Maps an observed thread snapshot onto the review-archive store. Touches the parent pull request
///     and upserts the thread with its comments, preserving the authorship already decided by the
///     producer. This is a passive observer and does not affect review behavior.
/// </summary>
public sealed class ReviewArchiveIngestionService(IReviewArchiveStore store) : IReviewArchiveIngestionService
{
    public async Task HandleThreadUpdatedAsync(ThreadUpdatedEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var key = new PullRequestRetentionKey(
            evt.ClientId,
            evt.ConnectionId,
            evt.RepositoryId,
            evt.PullRequestId);

        await store.TouchPullRequestAsync(key, evt.Status, evt.LastActivityAt, ct);

        var snapshot = new RetainedThreadSnapshot(
            evt.ThreadId,
            evt.FilePath,
            evt.Line,
            evt.Status,
            evt.LastActivityAt,
            evt.Comments
                .Select(comment => new RetainedCommentSnapshot(
                    comment.CommentId,
                    comment.AuthorIdentity,
                    comment.IsAiAuthored,
                    comment.PublishedAt,
                    comment.Text,
                    comment.OriginatingJobId))
                .ToList());

        await store.UpsertThreadAsync(key, snapshot, ct);
    }

    public async Task HandleReviewIncrementDiffsAsync(ReviewIncrementCompletedEvent evt, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var key = new PullRequestRetentionKey(
            evt.ClientId,
            evt.ConnectionId,
            evt.RepositoryId,
            evt.PullRequestId);

        await store.TouchPullRequestAsync(key, evt.PullRequestState, evt.LastActivityAt, ct);

        var fileDiffs = evt.FileDiffs
            .Select(fileDiff => new RetainedFileDiffSnapshot(
                fileDiff.FilePath,
                fileDiff.ChangeType,
                fileDiff.IsBinary,
                fileDiff.UnifiedDiff))
            .ToList();

        await store.SaveFileDiffsAsync(key, evt.RevisionKey, fileDiffs, ct);
    }
}
