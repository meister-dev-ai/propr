// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.CodeInsights.Contracts;

/// <summary>
///     Persistence boundary for the pull-request aggregate and the findings collected under it. Finding text is
///     encrypted at rest and all structured metadata stays queryable.
/// </summary>
/// <remarks>
///     Independent of the review, memory, and review-archive tables; the link back to them is by (pull request id +
///     provider thread id) values, never a foreign key. The outcomes, harvested threads, classification state and
///     retention of these records are separate ports, so a consumer depends on the one thing it writes and a new
///     kind of collected record arrives as its own boundary rather than widening this one.
/// </remarks>
public interface ICodeInsightFindingStore
{
    /// <summary>
    ///     Upserts the pull-request aggregate identified by <paramref name="key" /> and sets its lifecycle
    ///     state and latest activity timestamp. Creates the aggregate when absent.
    /// </summary>
    /// <param name="key">Identity of the aggregate.</param>
    /// <param name="pullRequestState">Last-known lifecycle state.</param>
    /// <param name="lastActivityAt">Collection activity timestamp; the retention anchor only moves forward.</param>
    /// <param name="repositoryName">
    ///     The repository's display name, when the caller knows one. Refreshed on every touch so a rename catches
    ///     up; a caller that does not know the name leaves whatever is recorded alone rather than clearing it.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task TouchPullRequestAsync(
        CodeInsightPullRequestKey key,
        string pullRequestState,
        DateTimeOffset lastActivityAt,
        string? repositoryName = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Materialises <paramref name="findings" /> under the pull request identified by
    ///     <paramref name="key" />, assigning a surrogate identifier to each newly created record. The
    ///     parent aggregate is created if it does not yet exist.
    ///     Idempotent on the natural key (aggregate, revision key, ordinal): re-processing the same
    ///     increment refreshes the existing records in place and never creates duplicates, so the
    ///     surrogate identifiers already handed to downstream consumers stay valid.
    ///     Returns the number of records newly created.
    /// </summary>
    Task<int> MaterialiseFindingsAsync(
        CodeInsightPullRequestKey key,
        Guid jobId,
        string revisionKey,
        DateTimeOffset observedAt,
        IReadOnlyList<CodeInsightFindingSnapshot> findings,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns the materialised findings (with decrypted message text) for the pull request identified
    ///     by <paramref name="key" />, ordered by revision key then ordinal. Empty when nothing was
    ///     collected for it.
    /// </summary>
    Task<IReadOnlyList<CodeInsightFindingView>> GetFindingsForPullRequestAsync(
        CodeInsightPullRequestKey key,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns the finding a provider thread was posted as, or <see langword="null" /> when the thread
    ///     does not correspond to a collected finding, for example when it was raised before collection
    ///     was enabled for the client. Callers must treat null as "skip", never as a reason to create a
    ///     placeholder record.
    /// </summary>
    Task<CodeInsightFindingView?> FindByProviderThreadAsync(
        Guid clientId,
        string repositoryId,
        long pullRequestId,
        string providerThreadId,
        CancellationToken ct = default);
}
