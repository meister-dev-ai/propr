// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Domain.Events;

namespace MeisterDev.ProPR.Application.Features.Crawling.Execution.Services;

/// <summary>
///     Builds the thread event both thread observers consume, from a provider thread and the pass's ownership
///     answer.
/// </summary>
/// <remarks>
///     Shared by the active crawl pass and the close observation so both derive the event's shape, its comment
///     identities and its activity timestamp the same way. The ownership resolver they hand in is not the same:
///     the active pass reaches a provider adapter that contributes the account ProPR posts under, while the
///     close observation has only the recorded provenance. A comment ProPR posted with no provenance row is
///     therefore recognised on the active pass and not at the close.
/// </remarks>
internal static class ThreadUpdatedEventFactory
{
    public static ThreadUpdatedEvent Build(
        Guid clientId,
        Guid connectionId,
        string repositoryId,
        long pullRequestId,
        PrCommentThread thread,
        ThreadOwnershipResolver ownership)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(ownership);

        var comments = new List<ThreadUpdatedComment>(thread.Comments.Count);
        var lastActivityAt = DateTimeOffset.MinValue;
        var threadId = thread.ThreadId!;

        foreach (var comment in thread.Comments)
        {
            var publishedAt = comment.PublishedAt ?? DateTimeOffset.UtcNow;
            if (publishedAt > lastActivityAt)
            {
                lastActivityAt = publishedAt;
            }

            var commentId = comment.CommentId.ToString(CultureInfo.InvariantCulture);
            var commentRef = new ThreadCommentRef(threadId, commentId, comment.AuthorId, comment.AuthorName);

            comments.Add(
                new ThreadUpdatedComment(
                    commentId,
                    ResolveAuthorIdentity(comment),
                    ownership.OwnsComment(commentRef),
                    publishedAt,
                    comment.Content,
                    ownership.ResolveOriginatingJobId(threadId, commentId),
                    comment.IsSystemGenerated));
        }

        if (lastActivityAt == DateTimeOffset.MinValue)
        {
            lastActivityAt = DateTimeOffset.UtcNow;
        }

        return new ThreadUpdatedEvent(
            clientId,
            connectionId,
            repositoryId,
            pullRequestId,
            threadId,
            thread.FilePath,
            thread.LineNumber,
            thread.Status ?? "Active",
            lastActivityAt,
            comments);
    }

    public static string ResolveAuthorIdentity(PrThreadComment comment)
    {
        ArgumentNullException.ThrowIfNull(comment);

        if (comment.AuthorId.HasValue && comment.AuthorId.Value != Guid.Empty)
        {
            return comment.AuthorId.Value.ToString("D");
        }

        return string.IsNullOrWhiteSpace(comment.AuthorName) ? "unknown" : comment.AuthorName;
    }
}
