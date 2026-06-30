// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.ReviewArchive;

/// <summary>
///     Persistence boundary for posted-comment provenance: a mapping from each provider-native comment
///     a review job posted back to that job. A later ingestion step uses this to stamp the originating
///     job onto retained comments. The store keeps no foreign keys to review or memory tables; its rows
///     share the lifecycle of the retained pull-request data and are purged alongside it.
/// </summary>
public interface IPostedCommentOriginStore
{
    /// <summary>
    ///     Records the supplied provenance <paramref name="entries" />, inserting new rows and updating the
    ///     job/timestamp of any that already exist under their natural key
    ///     (client + repository + pull request + provider thread + provider comment). Idempotent:
    ///     re-recording the same comment does not create a duplicate row.
    /// </summary>
    Task RecordAsync(IReadOnlyList<PostedCommentOriginEntry> entries, CancellationToken ct = default);

    /// <summary>
    ///     Returns the review job that posted the comment identified by
    ///     (<paramref name="clientId" />, <paramref name="repositoryId" />, <paramref name="pullRequestId" />,
    ///     <paramref name="providerCommentId" />), or null when no provenance row is retained for it.
    ///     Resolution is comment-id-primary: among the pull request's origins with this comment id, a single
    ///     match wins outright; only when several share the comment id (the Azure DevOps thread-local
    ///     collision) does <paramref name="providerThreadId" /> disambiguate them. The thread id is therefore
    ///     advisory — providers whose comment ids are globally unique within a pull request
    ///     (GitHub/GitLab/Forgejo) resolve on the comment id alone and may pass a non-matching or null thread
    ///     id without breaking attribution.
    /// </summary>
    Task<Guid?> GetJobIdForCommentAsync(
        Guid clientId,
        string repositoryId,
        long pullRequestId,
        string? providerThreadId,
        string providerCommentId,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns the raw provenance rows for one pull request so a stamping pass can resolve attribution
    ///     comment-id-primary (see <see cref="GetJobIdForCommentAsync" />): match by comment id and only fall
    ///     back to the thread id to break a collision when several rows share a comment id. Resolves the whole
    ///     pull request in a single query so the pass need not issue one lookup per comment. An empty list
    ///     means no provenance is retained for the pull request.
    /// </summary>
    Task<IReadOnlyList<PostedCommentOriginRow>> GetJobIdsForPullRequestAsync(
        Guid clientId,
        string repositoryId,
        long pullRequestId,
        CancellationToken ct = default);

    /// <summary>
    ///     Deletes every provenance row for the supplied pull request. Returns the number of rows removed.
    /// </summary>
    Task<int> PurgeForPullRequestAsync(
        Guid clientId,
        string repositoryId,
        long pullRequestId,
        CancellationToken ct = default);

    /// <summary>
    ///     Deletes every provenance row whose pull request belongs to one of the supplied
    ///     <paramref name="pullRequests" />. Mirrors the per-connection retention purge, which resolves the
    ///     connection's retained pull requests and then removes their provenance in one pass. Returns the
    ///     number of rows removed.
    /// </summary>
    Task<int> PurgeForPullRequestsAsync(
        IReadOnlyList<PostedCommentOriginPullRequestRef> pullRequests,
        CancellationToken ct = default);
}

/// <summary>
///     Identifies a single pull request for provenance purge, scoped by client and repository.
/// </summary>
/// <param name="ClientId">Owning client.</param>
/// <param name="RepositoryId">Provider repository identifier.</param>
/// <param name="PullRequestId">Provider pull-request identifier.</param>
public sealed record PostedCommentOriginPullRequestRef(
    Guid ClientId,
    string RepositoryId,
    long PullRequestId);

/// <summary>
///     One retained provenance row for a pull request: the provider thread id (when the provider exposes
///     one), the provider comment id, and the review job that posted it. Stamping resolves attribution
///     comment-id-primary — by comment id, falling back to the thread id only to break a collision when
///     several rows of one pull request share a comment id (Azure DevOps scopes comment ids to a thread).
/// </summary>
/// <param name="ProviderThreadId">Provider thread identifier, or null when the provider exposes none.</param>
/// <param name="ProviderCommentId">Provider-native comment identifier.</param>
/// <param name="JobId">Review job that posted the comment.</param>
public readonly record struct PostedCommentOriginRow(
    string? ProviderThreadId,
    string ProviderCommentId,
    Guid JobId);
