// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.ReviewArchive;

/// <summary>
///     Persistence boundary for opt-in-retained raw pull-request data. The store encrypts the raw
///     text (comment bodies, unified diffs) at rest and keeps all structured metadata queryable.
///     It is independent of the review and memory tables; the only link back to memory is by
///     (pull request id + thread id) values, never a foreign key.
/// </summary>
public interface IReviewArchiveStore
{
    /// <summary>
    ///     Upserts the retained pull-request row identified by <paramref name="key" /> and sets its
    ///     latest activity timestamp and lifecycle state. Creates the row when absent.
    /// </summary>
    Task TouchPullRequestAsync(
        PullRequestRetentionKey key,
        string prState,
        DateTimeOffset lastActivityAt,
        CancellationToken ct = default);

    /// <summary>
    ///     Upserts a full thread snapshot under the pull request identified by <paramref name="key" />,
    ///     replacing the thread's stored comments to match the snapshot exactly (idempotent). The parent
    ///     pull request is created if it does not yet exist. Comment bodies are encrypted on write.
    /// </summary>
    Task UpsertThreadAsync(
        PullRequestRetentionKey key,
        RetainedThreadSnapshot thread,
        CancellationToken ct = default);

    /// <summary>
    ///     Adds or replaces the per-file diffs for the supplied <paramref name="revisionKey" /> under the
    ///     pull request identified by <paramref name="key" />. The parent pull request is created if it
    ///     does not yet exist. Diff text is encrypted on write.
    /// </summary>
    Task SaveFileDiffsAsync(
        PullRequestRetentionKey key,
        string revisionKey,
        IReadOnlyList<RetainedFileDiffSnapshot> fileDiffs,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns every retained thread (with decrypted comments) for the pull request identified by
    ///     <paramref name="clientId" />, <paramref name="repositoryId" />, and <paramref name="pullRequestId" />,
    ///     or an empty list when the pull request is not retained. The owning connection is resolved from
    ///     the retained data itself; when more than one connection retained the same pull request the most
    ///     recently active one is used.
    /// </summary>
    Task<IReadOnlyList<RetainedThreadView>> GetThreadsForPullRequestAsync(
        Guid clientId,
        string repositoryId,
        long pullRequestId,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns a single retained file diff (with decrypted unified diff) for the pull request identified
    ///     by <paramref name="clientId" />, <paramref name="repositoryId" />, and <paramref name="pullRequestId" />
    ///     and the supplied <paramref name="filePath" />. When <paramref name="revisionKey" /> is null the
    ///     newest retained revision for the file is returned. Returns null when no matching diff is retained.
    ///     The owning connection is resolved from the retained data itself; when more than one connection
    ///     retained the same pull request the most recently active one is used.
    /// </summary>
    Task<RetainedFileDiffView?> GetFileDiffAsync(
        Guid clientId,
        string repositoryId,
        long pullRequestId,
        string? revisionKey,
        string filePath,
        CancellationToken ct = default);

    /// <summary>
    ///     Lists the retained files for the pull request identified by <paramref name="clientId" />,
    ///     <paramref name="repositoryId" />, and <paramref name="pullRequestId" />, collapsing each file path
    ///     to its newest retained revision. The (encrypted) diff text is not decrypted or returned. Returns an
    ///     empty list when the pull request is not retained or has no retained diffs. The owning connection is
    ///     resolved from the retained data itself; when more than one connection retained the same pull request
    ///     the most recently active one is used.
    /// </summary>
    Task<IReadOnlyList<RetainedFileSummaryView>> ListRetainedFilesForPullRequestAsync(
        Guid clientId,
        string repositoryId,
        long pullRequestId,
        CancellationToken ct = default);

    /// <summary>
    ///     Deletes retained pull requests whose last activity is strictly older than
    ///     <paramref name="cutoff" />, cascading to their threads, comments, and diffs. Returns the
    ///     number of pull requests removed.
    /// </summary>
    Task<int> PurgeExpiredAsync(DateTimeOffset cutoff, CancellationToken ct = default);

    /// <summary>
    ///     Deletes every retained pull request for the supplied <paramref name="connectionId" />,
    ///     cascading to their threads, comments, and diffs. Returns the number of pull requests removed.
    /// </summary>
    Task<int> PurgeForConnectionAsync(Guid connectionId, CancellationToken ct = default);

    /// <summary>
    ///     Deletes retained pull requests for the supplied <paramref name="connectionId" /> whose last
    ///     activity is strictly older than <paramref name="cutoff" />, cascading to their threads,
    ///     comments, and diffs. The pull-request lifecycle state is ignored, so open pull requests past
    ///     the cutoff are removed too. Returns the number of pull requests removed.
    /// </summary>
    Task<int> PurgeExpiredForConnectionAsync(
        Guid connectionId,
        DateTimeOffset cutoff,
        CancellationToken ct = default);

    /// <summary>
    ///     Lists the identity of every retained pull request for the supplied <paramref name="connectionId" />
    ///     that a connection-scoped purge would touch. When <paramref name="cutoff" /> is null all of the
    ///     connection's retained pull requests are returned (matching <see cref="PurgeForConnectionAsync" />);
    ///     otherwise only those with last activity strictly older than the cutoff are returned (matching
    ///     <see cref="PurgeExpiredForConnectionAsync" />). The refs let provenance data that shares the
    ///     retained data's lifecycle be purged for exactly the same pull requests.
    /// </summary>
    Task<IReadOnlyList<RetainedPullRequestRef>> ListPullRequestRefsForConnectionAsync(
        Guid connectionId,
        DateTimeOffset? cutoff,
        CancellationToken ct = default);
}

/// <summary>
///     The client-scoped identity of a single retained pull request, used to purge provenance data that
///     shares the retained pull request's lifecycle.
/// </summary>
/// <param name="ClientId">Owning client.</param>
/// <param name="RepositoryId">Provider repository identifier.</param>
/// <param name="PullRequestId">Provider pull-request identifier.</param>
public sealed record RetainedPullRequestRef(
    Guid ClientId,
    string RepositoryId,
    long PullRequestId);
