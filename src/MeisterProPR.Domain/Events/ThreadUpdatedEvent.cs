// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Events;

/// <summary>
///     Raised by the crawl/sync path when a connection opts in to thread retention. Carries the full
///     current snapshot of an observed pull-request comment thread so a passive archive consumer can
///     persist it without any further provider calls. Raising this event never influences review
///     decisions, deduplication, memory, or the scope snapshot.
/// </summary>
/// <param name="ClientId">The client that owns the pull request.</param>
/// <param name="ConnectionId">The SCM provider connection the pull request belongs to.</param>
/// <param name="RepositoryId">The provider repository identifier.</param>
/// <param name="PullRequestId">The provider pull-request identifier.</param>
/// <param name="ThreadId">The provider thread identifier (unique within the pull request).</param>
/// <param name="FilePath">The file path the thread is anchored to, or <c>null</c> for pull-request-level threads.</param>
/// <param name="Line">The line the thread is anchored to, or <c>null</c> for file- or pull-request-level threads.</param>
/// <param name="Status">The last-known thread status.</param>
/// <param name="LastActivityAt">The UTC timestamp of the latest observed activity on the thread.</param>
/// <param name="Comments">The full set of comments belonging to the thread, in publication order.</param>
public sealed record ThreadUpdatedEvent(
    Guid ClientId,
    Guid ConnectionId,
    string RepositoryId,
    long PullRequestId,
    string ThreadId,
    string? FilePath,
    int? Line,
    string Status,
    DateTimeOffset LastActivityAt,
    IReadOnlyList<ThreadUpdatedComment> Comments);

/// <summary>
///     A single comment within a <see cref="ThreadUpdatedEvent" />. Authorship is decided once by the
///     producer and carried on the event; consumers must not recompute it.
/// </summary>
/// <param name="CommentId">The provider comment identifier.</param>
/// <param name="AuthorIdentity">The provider-neutral author identity.</param>
/// <param name="IsAiAuthored">Whether the comment was authored by the AI reviewer/bot identity.</param>
/// <param name="PublishedAt">The UTC timestamp the comment was published on the provider.</param>
/// <param name="Text">The comment body text.</param>
/// <param name="OriginatingJobId">
///     The review job that produced this comment, when its provenance is retained; null otherwise. Set by
///     the producer from a passive provenance side-read and never influences review behavior.
/// </param>
public sealed record ThreadUpdatedComment(
    string CommentId,
    string AuthorIdentity,
    bool IsAiAuthored,
    DateTimeOffset PublishedAt,
    string Text,
    Guid? OriginatingJobId = null);
