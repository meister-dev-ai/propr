// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>Interface for managing review jobs in the repository.</summary>
public interface IJobRepository : IReviewFileResultStore
{
    /// <summary>Persists a new review job.</summary>
    Task AddAsync(ReviewJob job, CancellationToken ct = default);

    /// <summary>
    ///     Atomically adds a review job when no matching active duplicate exists for the same PR identity.
    ///     For provider-neutral revisions, active jobs for the same PR but older revisions are cancelled before insert.
    /// </summary>
    Task<TryAddReviewJobResult> TryAddIfNoActiveDuplicateAsync(ReviewJob job, CancellationToken ct = default);

    /// <summary>Returns the first Pending or Processing job for the given PR iteration, or null.</summary>
    /// <param name="organizationUrl">Base URL of the Azure DevOps organization.</param>
    /// <param name="projectId">ID of the Azure DevOps project.</param>
    /// <param name="repositoryId">ID of the repository containing the pull request.</param>
    /// <param name="pullRequestId">Numeric ID of the pull request.</param>
    /// <param name="iterationId">ID of the pull request iteration.</param>
    ReviewJob? FindActiveJob(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId);

    /// <summary>Returns the most-recent Completed job for the given PR iteration, or null.</summary>
    ReviewJob? FindCompletedJob(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId);

    /// <summary>
    ///     Returns the most-recent Failed job for the given PR iteration, or null.
    ///     Used to suppress automatic re-enqueue of a review that already failed at the same revision,
    ///     so that deterministic failures do not loop and require a manual restart instead.
    /// </summary>
    ReviewJob? FindFailedJob(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId);

    /// <summary>All jobs for a client, newest first.</summary>
    /// <param name="clientId">The client identifier to filter jobs by.</param>
    IReadOnlyList<ReviewJob> GetAllForClient(Guid clientId);

    /// <summary>Returns all jobs across all clients, newest first, with optional status filter and pagination.</summary>
    Task<(int total, IReadOnlyList<ReviewJob> items)> GetAllJobsAsync(
        int limit,
        int offset,
        JobStatus? status,
        Guid? clientId = null,
        int? pullRequestId = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns a projected page of the review overview list, newest first. Reads only the scalar fields the
    ///     overview renders: the summary comes from the denormalized column and token totals are summed in the
    ///     database, so the result blob and protocol rows are never materialized and the query runs untracked.
    /// </summary>
    Task<(int total, IReadOnlyList<JobListPageItemDto> items)> GetJobListPageAsync(
        int limit,
        int offset,
        JobStatus? status,
        Guid? clientId = null,
        int? pullRequestId = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns a projected page of the review overview list, newest first, restricted to the given client
    ///     identifiers. Pass <see langword="null" /> for <paramref name="clientIds" /> to skip the client filter
    ///     entirely, an empty collection to force an empty result, and a populated collection to read across
    ///     several clients in one query.
    /// </summary>
    Task<(int total, IReadOnlyList<JobListPageItemDto> items)> GetJobListPageAsync(
        int limit,
        int offset,
        JobStatus? status,
        IEnumerable<Guid>? clientIds,
        int? pullRequestId = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns a page of the review history grouped by pull request, most recently active first, together
    ///     with the total number of pull requests that match. Each group carries every run against that pull
    ///     request and the rollups shown beside it.
    /// </summary>
    /// <remarks>
    ///     <paramref name="limit" /> and <paramref name="offset" /> page over pull requests, not runs, which is
    ///     the grain the history is read at. A caller that pages over runs instead has to hold the whole history
    ///     to group it, and so can never show a complete history without fetching one.
    /// </remarks>
    Task<(int total, IReadOnlyList<PullRequestHistoryGroupDto> items)> GetPullRequestHistoryPageAsync(
        int limit,
        int offset,
        JobStatus? status,
        Guid? clientId = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns a page of the review history grouped by pull request, restricted to the given client
    ///     identifiers. Pass <see langword="null" /> for <paramref name="clientIds" /> to skip the client filter,
    ///     an empty collection to force an empty result, and a populated collection to read across several
    ///     clients in one query.
    /// </summary>
    Task<(int total, IReadOnlyList<PullRequestHistoryGroupDto> items)> GetPullRequestHistoryPageAsync(
        int limit,
        int offset,
        JobStatus? status,
        IEnumerable<Guid>? clientIds,
        CancellationToken ct = default);

    /// <summary>Gets a job by id, or null if not found.</summary>
    ReviewJob? GetById(Guid id);

    /// <summary>Returns all jobs with Status == Pending, oldest first.</summary>
    IReadOnlyList<ReviewJob> GetPendingJobs();

    /// <summary>
    ///     Returns at most <paramref name="limit" /> jobs eligible to be claimed, oldest first. Bounded on
    ///     purpose: every host polls this, and a deep queue must not turn each poll cycle into a full scan of
    ///     everything pending. They are candidates only, since the claim decides who actually gets one.
    ///     <para>
    ///         <paramref name="submittedAfter" /> continues from an earlier window's last candidate, so a
    ///         caller whose whole window was ineligible can page deeper instead of starving whatever sits
    ///         behind it. Submission times are effectively unique; a tie missed by the cursor is picked up
    ///         on the next cycle.
    ///     </para>
    /// </summary>
    Task<IReadOnlyList<ReviewJob>> GetClaimCandidatesAsync(
        int limit,
        DateTimeOffset? submittedAfter = null,
        CancellationToken ct = default);

    /// <summary>Returns all jobs currently in the Processing state.</summary>
    Task<IReadOnlyList<ReviewJob>> GetProcessingJobsAsync(CancellationToken ct = default);

    /// <summary>Returns the number of jobs currently in the Processing state.</summary>
    Task<int> CountProcessingJobsAsync(CancellationToken ct = default);

    /// <summary>Updates the retry count for a review job.</summary>
    Task UpdateRetryCountAsync(Guid id, int retryCount, CancellationToken ct = default);

    /// <summary>
    ///     Persists the in-scope changed-file count (denominator of the "files reviewed" progress metric),
    ///     fixed once at dispatch planning. No-op if the job does not exist.
    /// </summary>
    /// <summary>
    ///     Counts the per-file results for a job that reached a terminal successful state — the live numerator
    ///     of the "files reviewed" progress metric. Excludes excluded, failed, and carried-forward files, and is a
    ///     projection-only count that never materializes file-result text. Returns 0 when the job has no results.
    /// </summary>
    Task<int> CountReviewedFilesAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Marks the job as failed with an error message.</summary>
    Task SetFailedAsync(Guid id, string errorMessage, CancellationToken ct = default);

    /// <summary>Deletes a job by id. No-op if the job does not exist.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Sets the review result for a completed job.</summary>
    Task SetResultAsync(Guid id, ReviewResult result, CancellationToken ct = default);

    /// <summary>
    ///     Atomic compare-and-swap on Status.
    ///     Returns <c>false</c> if the current status does not match <paramref name="from" />.
    /// </summary>
    Task<bool> TryTransitionAsync(Guid id, JobStatus from, JobStatus to, CancellationToken ct = default);

    /// <summary>
    ///     Returns the <see cref="ReviewJob" /> with <c>FileReviewResults</c>
    ///     eagerly loaded, or <see langword="null" /> if no job with the given id exists.
    /// </summary>
    /// <summary>Adds a per-file review result for a job.</summary>
    /// <summary>Updates a per-file review result for a job.</summary>
    /// <summary>
    ///     Returns the <see cref="ReviewJob" /> with <c>Protocols</c> and <c>Protocols.Events</c>
    ///     eagerly loaded, or <see langword="null" /> if no job with the given id exists.
    /// </summary>
    Task<ReviewJob?> GetByIdWithProtocolsAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    ///     Returns the <see cref="ReviewJob" /> equivalent to <see cref="GetByIdWithProtocolsAsync" /> for the
    ///     read-only protocol overview, except each <see cref="ProtocolEvent" /> is loaded WITHOUT its
    ///     <see cref="ProtocolEvent.PhaseTimings" /> jsonb column. The overview neither serializes nor reads phase
    ///     timings server-side, and on heavy traces that column dominates the load; excluding it keeps the polled
    ///     overview responsive. The text columns (<c>InputTextSample</c>/<c>SystemPrompt</c>/<c>OutputSummary</c>)
    ///     remain so the reader's pass-badge resolvers keep working. Returns <see langword="null" /> if no job with
    ///     the given id exists.
    /// </summary>
    Task<ReviewJob?> GetByIdWithProtocolsForOverviewAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    ///     Returns the <see cref="ReviewJob" /> with <c>Protocol</c> and <c>Protocol.Events</c>
    ///     eagerly loaded, or <see langword="null" /> if no job with the given id exists.
    ///     This is the only sanctioned path for reading protocol data (ReviewJob is the aggregate root).
    /// </summary>
    [Obsolete("Use GetByIdWithProtocolsAsync instead.")]
    Task<ReviewJob?> GetByIdWithProtocolAsync(Guid id, CancellationToken ct = default);

    /// <summary>Marks the job as cancelled. No-op if the job does not exist or is already in a terminal state.</summary>
    Task SetCancelledAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    ///     Marks the job as superseded because a newer push arrived for the same pull request.
    ///     No-op if the job does not exist or is already in a terminal state.
    /// </summary>
    Task SetSupersededAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    ///     Marks the job as stopped because a client administrator halted it manually through the control
    ///     panel. No-op if the job does not exist or is already in a terminal state.
    /// </summary>
    Task SetStoppedAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    ///     Marks the job budget-exceeded because a hard cap was reached mid-review, recording the binding scope,
    ///     cap kind, threshold, and spend as the reason. No-op if the job does not exist or is already in a
    ///     deliberate terminal state (completed, failed, cancelled, superseded, or stopped).
    /// </summary>
    Task SetBudgetExceededAsync(
        Guid id,
        BudgetScopeKind scope,
        BudgetCapKind capKind,
        decimal thresholdUsd,
        decimal spentUsd,
        CancellationToken ct = default);

    /// <summary>
    ///     Marks a queued job budget-held because a cap was already reached at admission, recording the binding
    ///     scope, cap kind, threshold, and spend as the reason. No-op unless the job is currently pending.
    /// </summary>
    Task SetBudgetHeldAsync(
        Guid id,
        BudgetScopeKind scope,
        BudgetCapKind capKind,
        decimal thresholdUsd,
        decimal spentUsd,
        CancellationToken ct = default);

    /// <summary>Returns all Pending or Processing jobs for the given ADO organisation/project combination.</summary>
    Task<IReadOnlyList<ReviewJob>> GetActiveJobsForConfigAsync(
        string organizationUrl,
        string projectId,
        CancellationToken ct = default);

    /// <summary>
    ///     The repository identity a previous review of this pull request recorded, or
    ///     <see langword="null" /> when none has run or the answer would be ambiguous.
    /// </summary>
    /// <remarks>
    ///     Configuration records a repository by name, so a webhook has no reason to hold the provider's
    ///     identity for it. A review that has already run does hold it, and holds the one the review
    ///     itself used, which makes history a better source than asking the provider again: no
    ///     credential, no network call, and nothing to fail.
    ///     <para>
    ///         A pull request number is only unique within a repository on GitLab and Forgejo, so the
    ///         number alone cannot identify one. Jobs carrying the requested repository name are preferred,
    ///         and an answer is given only when the candidates agree on a single identity.
    ///     </para>
    /// </remarks>
    Task<string?> FindRecordedRepositoryIdAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryName,
        int pullRequestId,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns jobs for a specific pull request, newest first, with pagination.
    ///     Includes <c>Protocols</c> and <c>Protocols.Events</c> eagerly loaded.
    /// </summary>
    Task<IReadOnlyList<ReviewJob>> GetByPrAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int page,
        int pageSize,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns the most-recent Completed job for the given PR iteration with
    ///     <see cref="ReviewJob.FileReviewResults" /> eagerly loaded, or <see langword="null" />.
    /// </summary>
    Task<ReviewJob?> GetCompletedJobWithFileResultsAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns the most-recent Completed job for the given pull request whose persisted revision key matches
    ///     the supplied stored revision key, with <see cref="ReviewJob.FileReviewResults" /> eagerly loaded, or
    ///     <see langword="null" />.
    /// </summary>
    Task<ReviewJob?> GetCompletedJobWithFileResultsByStoredRevisionAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        string storedRevisionKey,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns the most-recent terminal review job usable as a cross-revision carry-forward baseline for the
    ///     given pull request: any job in a terminal state (<see cref="JobStatus.Completed" />,
    ///     <see cref="JobStatus.Failed" />, <see cref="JobStatus.Cancelled" />, or
    ///     <see cref="JobStatus.Superseded" />) that is not <paramref name="excludeJobId" /> and whose stored
    ///     revision key differs from <paramref name="currentRevisionKey" />. Candidates are ranked by count of
    ///     usable reviewed file results (descending) and then most-recent completion, with plain
    ///     <see cref="JobStatus.Cancelled" /> (abandoned pull request) deprioritized. <see cref="ReviewJob.FileReviewResults" />
    ///     is eagerly loaded. Returns <see langword="null" /> when no such job exists.
    /// </summary>
    Task<ReviewJob?> GetLatestReusableTerminalJobAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid excludeJobId,
        string currentRevisionKey,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns the revision the given client last engaged with for the given pull request, or
    ///     <see langword="null" /> when that client has no job on record for it. The result is scoped to
    ///     <paramref name="clientId" />: two clients configured against the same repository engage independently.
    /// </summary>
    /// <remarks>
    ///     A job in any status counts, including a still-running one, because a first review in flight is engagement.
    ///     The exceptions are <see cref="JobStatus.BudgetHeld" /> and <see cref="JobStatus.BudgetExceeded" />: a job
    ///     blocked at a budget cap is resumed by restarting it once budget frees, so counting it would leave the pull
    ///     request permanently at a revision no review was completed for.
    /// </remarks>
    Task<EngagedReviewRevision?> GetLatestEngagedRevisionAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns the terminal review job for the given pull request and persisted revision key that has the most
    ///     reusable completed file results, with <see cref="ReviewJob.FileReviewResults" /> eagerly loaded, or
    ///     <see langword="null" />.
    /// </summary>
    Task<ReviewJob?> GetBestTerminalJobWithFileResultsByStoredRevisionAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        string storedRevisionKey,
        CancellationToken ct = default);

    /// <summary>Persists the AI connection snapshot captured at job-start time.</summary>
    Task UpdateAiConfigAsync(Guid id, Guid? connectionId, string? model, CancellationToken ct = default, float? reviewTemperature = null);

    /// <summary>Persists the PR context snapshot captured after job creation.</summary>
    Task UpdatePrContextAsync(
        Guid id,
        string? prTitle,
        string? prRepositoryName,
        string? prSourceBranch,
        string? prTargetBranch,
        CancellationToken ct = default);
}
