// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Collections.Concurrent;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Admission.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Support;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     In-memory <see cref="IJobRepository" /> used by offline review execution.
/// </summary>
public sealed class InMemoryReviewJobRepository : IJobRepository
{
    private readonly ConcurrentDictionary<Guid, ReviewJob> _jobs = new();

    /// <summary>
    ///     Serialises every read and write that decides a job's status. The dictionary is concurrent, the
    ///     entities in it are not, and a claim, a completion, a failure, an admission decision and a supersede
    ///     are taken by different threads against the same fields. A check followed by an unguarded assignment
    ///     lets the later write win whichever order the two arrived in, so the check and the assignment are
    ///     taken together here.
    /// </summary>
    private readonly object _statusLock = new();

    public Task AddAsync(ReviewJob job, CancellationToken ct = default)
    {
        // Under the same lock as the atomic add: an insert made beside it can land between that operation's
        // duplicate scan and its own insert, which is how a second active job for one pull request appears.
        lock (this._statusLock)
        {
            this._jobs[job.Id] = job;
        }

        return Task.CompletedTask;
    }

    public Task<TryAddReviewJobResult> TryAddIfNoActiveDuplicateAsync(ReviewJob job, CancellationToken ct = default)
    {
        // The contract calls this operation atomic, so the read, the duplicate check, the supersede of older
        // revisions and the insert are one step. Two submissions for the same pull request would otherwise
        // both see no active job and both insert, and a refusal decided between the read and the supersede
        // would be overwritten.
        lock (this._statusLock)
        {
            return this.TryAddIfNoActiveDuplicate(job);
        }
    }

    private Task<TryAddReviewJobResult> TryAddIfNoActiveDuplicate(ReviewJob job)
    {
        var currentRevisionKey = ReviewRevisionKeys.TryGetStoredKey(job.ReviewRevisionReference);
        var activeJobs = this._jobs.Values
            .Where(candidate => string.Equals(candidate.OrganizationUrl, job.OrganizationUrl, StringComparison.Ordinal)
                                && string.Equals(candidate.ProjectId, job.ProjectId, StringComparison.Ordinal)
                                && RepositoryMatches(candidate, job.RepositoryId, job.ProjectId)
                                && candidate.PullRequestId == job.PullRequestId
                                && candidate.Status is JobStatus.Pending or JobStatus.Processing or JobStatus.BudgetHeld or JobStatus.BudgetExceeded
                                    or JobStatus.AdmissionHeld)
            .ToList();

        if (!string.IsNullOrWhiteSpace(currentRevisionKey))
        {
            var duplicateJob = activeJobs.FirstOrDefault(candidate => string.Equals(
                ReviewRevisionKeys.GetStoredKey(candidate.ReviewRevisionReference, candidate.IterationId),
                currentRevisionKey,
                StringComparison.Ordinal));
            if (duplicateJob is not null)
            {
                return Task.FromResult(new TryAddReviewJobResult(false, duplicateJob, 0));
            }

            var cancelledSupersededJobCount = 0;
            foreach (var activeJob in activeJobs.Where(candidate => !string.Equals(
                         ReviewRevisionKeys.GetStoredKey(candidate.ReviewRevisionReference, candidate.IterationId),
                         currentRevisionKey,
                         StringComparison.Ordinal)))
            {
                if (activeJob.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Superseded)
                {
                    continue;
                }

                activeJob.Status = JobStatus.Superseded;
                activeJob.CompletedAt = DateTimeOffset.UtcNow;
                cancelledSupersededJobCount++;
            }

            this._jobs[job.Id] = job;
            return Task.FromResult(new TryAddReviewJobResult(true, null, cancelledSupersededJobCount));
        }

        var duplicateIterationJob = activeJobs.FirstOrDefault(candidate => candidate.IterationId == job.IterationId);
        if (duplicateIterationJob is not null)
        {
            return Task.FromResult(new TryAddReviewJobResult(false, duplicateIterationJob, 0));
        }

        this._jobs[job.Id] = job;
        return Task.FromResult(new TryAddReviewJobResult(true, null, 0));
    }

    public ReviewJob? FindActiveJob(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId)
    {
        return this._jobs.Values.FirstOrDefault(job =>
            string.Equals(job.OrganizationUrl, organizationUrl, StringComparison.Ordinal)
            && string.Equals(job.ProjectId, projectId, StringComparison.Ordinal)
            && RepositoryMatches(job, repositoryId, projectId)
            && job.PullRequestId == pullRequestId
            && job.IterationId == iterationId
            && job.Status is JobStatus.Pending or JobStatus.Processing or JobStatus.BudgetHeld or JobStatus.BudgetExceeded or JobStatus.AdmissionHeld);
    }

    public ReviewJob? FindCompletedJob(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId)
    {
        return this._jobs.Values
            .Where(job =>
                string.Equals(job.OrganizationUrl, organizationUrl, StringComparison.Ordinal)
                && string.Equals(job.ProjectId, projectId, StringComparison.Ordinal)
                && RepositoryMatches(job, repositoryId, projectId)
                && job.PullRequestId == pullRequestId
                && job.IterationId == iterationId
                && job.Status == JobStatus.Completed)
            .OrderByDescending(job => job.CompletedAt)
            .FirstOrDefault();
    }

    public ReviewJob? FindFailedJob(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId)
    {
        return this._jobs.Values
            .Where(job =>
                string.Equals(job.OrganizationUrl, organizationUrl, StringComparison.Ordinal)
                && string.Equals(job.ProjectId, projectId, StringComparison.Ordinal)
                && RepositoryMatches(job, repositoryId, projectId)
                && job.PullRequestId == pullRequestId
                && job.IterationId == iterationId
                && job.Status == JobStatus.Failed)
            .OrderByDescending(job => job.CompletedAt)
            .FirstOrDefault();
    }

    public IReadOnlyList<ReviewJob> GetAllForClient(Guid clientId)
    {
        return this._jobs.Values
            .Where(job => job.ClientId == clientId)
            .OrderByDescending(job => job.SubmittedAt)
            .ToList()
            .AsReadOnly();
    }

    public Task<(int total, IReadOnlyList<ReviewJob> items)> GetAllJobsAsync(
        int limit,
        int offset,
        JobStatus? status,
        Guid? clientId = null,
        int? pullRequestId = null,
        CancellationToken ct = default)
    {
        var query = this._jobs.Values.AsEnumerable();

        if (status.HasValue)
        {
            query = query.Where(job => job.Status == status.Value);
        }

        if (clientId.HasValue)
        {
            query = query.Where(job => job.ClientId == clientId.Value);
        }

        if (pullRequestId.HasValue)
        {
            query = query.Where(job => job.PullRequestId == pullRequestId.Value);
        }

        var ordered = query.OrderByDescending(job => job.SubmittedAt).ToList();
        var page = ordered.Skip(offset).Take(limit).ToList().AsReadOnly();
        return Task.FromResult<(int total, IReadOnlyList<ReviewJob> items)>((ordered.Count, page));
    }

    public Task<(int total, IReadOnlyList<JobListPageItemDto> items)> GetJobListPageAsync(
        int limit,
        int offset,
        JobStatus? status,
        Guid? clientId = null,
        int? pullRequestId = null,
        CancellationToken ct = default)
    {
        return this.GetJobListPageAsync(
            limit,
            offset,
            status,
            clientId.HasValue ? [clientId.Value] : null,
            pullRequestId,
            ct);
    }

    public Task<(int total, IReadOnlyList<JobListPageItemDto> items)> GetJobListPageAsync(
        int limit,
        int offset,
        JobStatus? status,
        IEnumerable<Guid>? clientIds,
        int? pullRequestId = null,
        CancellationToken ct = default)
    {
        var query = this._jobs.Values.AsEnumerable();

        if (status.HasValue)
        {
            query = query.Where(job => job.Status == status.Value);
        }

        if (clientIds is not null)
        {
            var idList = clientIds as IList<Guid> ?? clientIds.ToList();
            query = idList.Count == 0
                ? query.Where(_ => false)
                : query.Where(job => idList.Contains(job.ClientId));
        }

        if (pullRequestId.HasValue)
        {
            query = query.Where(job => job.PullRequestId == pullRequestId.Value);
        }

        var ordered = query.OrderByDescending(job => job.SubmittedAt).ToList();
        var page = ordered
            .Skip(offset)
            .Take(limit)
            .Select(job => new JobListPageItemDto(
                job.Id,
                job.ClientId,
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.IterationId,
                job.Status,
                job.SubmittedAt,
                job.ProcessingStartedAt,
                job.CompletedAt,
                job.ResultSummary is { Length: > 200 } long_ ? long_[..200] : job.ResultSummary,
                !string.IsNullOrEmpty(job.ResultSummary),
                job.ErrorMessage,
                job.TotalInputTokensAggregated ?? job.Protocols.Sum(p => p.TotalInputTokens) ?? 0L,
                job.TotalOutputTokensAggregated ?? job.Protocols.Sum(p => p.TotalOutputTokens) ?? 0L,
                job.PrTitle,
                job.PrSourceBranch,
                job.PrTargetBranch,
                job.PrRepositoryName,
                job.AiModel,
                job.FileReviewResults.Count(r => r.IsComplete && !r.IsFailed && !r.IsExcluded && !r.IsCarriedForward),
                job.InScopeChangedFileCount,
                job.TotalEstimatedCostUsd,
                job.CostIsApproximate,
                job.Status == JobStatus.Completed && job.BudgetBlockCapKind == BudgetCapKind.Soft))
            .ToList()
            .AsReadOnly();
        return Task.FromResult<(int total, IReadOnlyList<JobListPageItemDto> items)>((ordered.Count, page));
    }

    public async Task<(int total, IReadOnlyList<PullRequestHistoryGroupDto> items)> GetPullRequestHistoryPageAsync(
        int limit,
        int offset,
        JobStatus? status,
        Guid? clientId = null,
        CancellationToken ct = default)
    {
        return await this.GetPullRequestHistoryPageAsync(
            limit,
            offset,
            status,
            clientId.HasValue ? [clientId.Value] : null,
            ct);
    }

    public async Task<(int total, IReadOnlyList<PullRequestHistoryGroupDto> items)> GetPullRequestHistoryPageAsync(
        int limit,
        int offset,
        JobStatus? status,
        IEnumerable<Guid>? clientIds,
        CancellationToken ct = default)
    {
        var (_, all) = await this.GetJobListPageAsync(int.MaxValue, 0, status, clientIds, null, ct);

        var groups = all
            .GroupBy(j => (j.OrganizationUrl, j.ProjectId, j.RepositoryId, j.PullRequestId))
            .Select(g =>
            {
                var jobs = g
                    .OrderByDescending(j => j.Status is JobStatus.Processing or JobStatus.Pending)
                    .ThenByDescending(j => j.CompletedAt ?? j.ProcessingStartedAt ?? j.SubmittedAt)
                    .ToList();
                var newest = jobs[0];
                var anyPriced = jobs.Exists(j => j.TotalEstimatedCostUsd is not null);
                var anyUnpriced = jobs.Exists(j => j.TotalEstimatedCostUsd is null);

                return new PullRequestHistoryGroupDto(
                    g.Key.OrganizationUrl,
                    g.Key.ProjectId,
                    g.Key.RepositoryId,
                    g.Key.PullRequestId,
                    newest.ClientId,
                    newest.PrTitle,
                    newest.PrRepositoryName,
                    newest.PrSourceBranch,
                    newest.PrTargetBranch,
                    jobs.Max(j => j.CompletedAt ?? j.ProcessingStartedAt ?? j.SubmittedAt),
                    jobs.Sum(j => j.TotalInputTokens),
                    jobs.Sum(j => j.TotalOutputTokens),
                    anyPriced ? jobs.Sum(j => j.TotalEstimatedCostUsd ?? 0m) : null,
                    jobs.Exists(j => j.CostIsApproximate) || (anyPriced && anyUnpriced),
                    jobs);
            })
            .OrderByDescending(g => g.LatestActivityAt)
            .ToList();

        var pageItems = groups.Skip(offset).Take(limit).ToList().AsReadOnly();
        return (groups.Count, pageItems);
    }

    public ReviewJob? GetById(Guid id)
    {
        return this._jobs.TryGetValue(id, out var job) ? job : null;
    }

    public IReadOnlyList<ReviewJob> GetPendingJobs()
    {
        return this._jobs.Values
            .Where(job => job.Status == JobStatus.Pending)
            .OrderBy(job => job.SubmittedAt)
            .ToList()
            .AsReadOnly();
    }

    public Task<IReadOnlyList<ReviewJob>> GetClaimCandidatesAsync(
        int limit,
        DateTimeOffset? submittedAfter = null,
        CancellationToken ct = default)
    {
        var pending = this.GetPendingJobs().AsEnumerable();
        if (submittedAfter is { } cursor)
        {
            pending = pending.Where(job => job.SubmittedAt > cursor);
        }

        return Task.FromResult<IReadOnlyList<ReviewJob>>(limit < 1 ? [] : pending.Take(limit).ToList().AsReadOnly());
    }

    public Task<IReadOnlyList<ReviewJob>> GetProcessingJobsAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ReviewJob>>(
            this._jobs.Values
                .Where(job => job.Status == JobStatus.Processing)
                .ToList()
                .AsReadOnly());
    }

    public Task<int> CountProcessingJobsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(this._jobs.Values.Count(job => job.Status == JobStatus.Processing));
    }

    public Task<bool> TryTransitionAsync(Guid id, JobStatus from, JobStatus to, CancellationToken ct = default)
    {
        lock (this._statusLock)
        {
            if (!this._jobs.TryGetValue(id, out var job) || job.Status != from)
            {
                return Task.FromResult(false);
            }

            job.Status = to;
            if (to == JobStatus.Processing)
            {
                job.ProcessingStartedAt = DateTimeOffset.UtcNow;
            }

            return Task.FromResult(true);
        }
    }

    public Task UpdateRetryCountAsync(Guid id, int retryCount, CancellationToken ct = default)
    {
        if (this._jobs.TryGetValue(id, out var job))
        {
            job.RetryCount = retryCount;
        }

        return Task.CompletedTask;
    }

    public Task UpdateInScopeChangedFileCountAsync(Guid id, int count, CancellationToken ct = default)
    {
        if (this._jobs.TryGetValue(id, out var job))
        {
            job.SetInScopeChangedFileCount(count);
        }

        return Task.CompletedTask;
    }

    public Task<int> CountReviewedFilesAsync(Guid jobId, CancellationToken ct = default)
    {
        var count = this._jobs.TryGetValue(jobId, out var job)
            ? job.FileReviewResults.Count(r => r.IsComplete && !r.IsFailed && !r.IsExcluded && !r.IsCarriedForward)
            : 0;
        return Task.FromResult(count);
    }

    public Task SetFailedAsync(Guid id, string errorMessage, CancellationToken ct = default)
    {
        lock (this._statusLock)
        {
            if (this._jobs.TryGetValue(id, out var job) &&
                job.Status is not (JobStatus.Cancelled or JobStatus.Superseded or JobStatus.Stopped or JobStatus.AdmissionRefused))
            {
                job.ErrorMessage = errorMessage;
                job.Status = JobStatus.Failed;
                job.CompletedAt = DateTimeOffset.UtcNow;
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Fails a job while it is still processing, and reports whether this call is what failed it.
    /// </summary>
    /// <remarks>
    ///     The durable store decides this in one conditional statement, and a caller that acts on the outcome
    ///     — clearing the lease, reporting the execution as stopped — needs the same answer here.
    ///     <see cref="SetFailedAsync" /> refuses only the deliberate terminal states, so a job that reached
    ///     its own end state first would be failed by it and the caller would be told nothing about that.
    /// </remarks>
    public bool TryFailWhileProcessing(Guid id, string errorMessage, ReviewJobFailureReason reason)
    {
        lock (this._statusLock)
        {
            if (!this._jobs.TryGetValue(id, out var job) || job.Status != JobStatus.Processing)
            {
                return false;
            }

            job.ErrorMessage = errorMessage;
            job.SetFailureReason(reason);
            job.Status = JobStatus.Failed;
            job.CompletedAt = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        this._jobs.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task SetResultAsync(Guid id, ReviewResult result, CancellationToken ct = default)
    {
        // Under the same lock as the failing transitions: a review completes on its own thread while the
        // heartbeat may be failing the job on another, and the two decide the same fields.
        lock (this._statusLock)
        {
            if (this._jobs.TryGetValue(id, out var job) &&
                job.Status is not (JobStatus.Cancelled or JobStatus.Superseded or JobStatus.Stopped or JobStatus.AdmissionRefused))
            {
                job.ApplyResult(result);
                job.Status = JobStatus.Completed;
                job.CompletedAt = DateTimeOffset.UtcNow;

                // Mirror the persistent store: a per-increment soft-capped run completes normally but records
                // the breach so it can be surfaced as soft-capped, distinct from a hard cut.
                if (result.BudgetSoftCapped
                    && result.BudgetSoftCapThresholdUsd is { } softCapThreshold
                    && result.BudgetSoftCapSpentUsd is { } softCapSpent)
                {
                    job.SetBudgetBlock(BudgetScopeKind.Increment, BudgetCapKind.Soft, softCapThreshold, softCapSpent);
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task<ReviewJob?> GetByIdWithFileResultsAsync(Guid id, CancellationToken ct = default)
    {
        return Task.FromResult(this.GetById(id));
    }

    public Task AddFileResultAsync(ReviewFileResult result, CancellationToken ct = default)
    {
        if (this._jobs.TryGetValue(result.JobId, out var job) && job.FileReviewResults.All(existing => existing.Id != result.Id))
        {
            job.FileReviewResults.Add(result);
        }

        return Task.CompletedTask;
    }

    public Task UpdateFileResultAsync(ReviewFileResult result, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task<ReviewJob?> GetByIdWithProtocolsAsync(Guid id, CancellationToken ct = default)
    {
        return Task.FromResult(this.GetById(id));
    }

    public Task<ReviewJob?> GetByIdWithProtocolsForOverviewAsync(Guid id, CancellationToken ct = default)
    {
        // Offline store holds the full in-memory graph (no DB, no phase_timings load cost), so the
        // projected-overview optimization is a no-op here: return the same job (correctness over perf).
        return this.GetByIdWithProtocolsAsync(id, ct);
    }

    [Obsolete("Use GetByIdWithProtocolsAsync instead.")]
    public Task<ReviewJob?> GetByIdWithProtocolAsync(Guid id, CancellationToken ct = default)
    {
        return this.GetByIdWithProtocolsAsync(id, ct);
    }

    public Task SetCancelledAsync(Guid id, CancellationToken ct = default)
    {
        lock (this._statusLock)
        {
            if (this._jobs.TryGetValue(id, out var job) &&
                job.Status is not (JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Superseded or JobStatus.Stopped
                    or JobStatus.AdmissionRefused))
            {
                job.Status = JobStatus.Cancelled;
                job.CompletedAt = DateTimeOffset.UtcNow;
            }
        }

        return Task.CompletedTask;
    }

    public Task SetSupersededAsync(Guid id, CancellationToken ct = default)
    {
        lock (this._statusLock)
        {
            if (this._jobs.TryGetValue(id, out var job) &&
                job.Status is not (JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Superseded or JobStatus.Stopped
                    or JobStatus.AdmissionRefused))
            {
                job.Status = JobStatus.Superseded;
                job.CompletedAt = DateTimeOffset.UtcNow;
            }
        }

        return Task.CompletedTask;
    }

    public Task<SupersededReviewJobState?> TrySupersedeAsync(
        Guid id,
        JobStatus expectedStatus,
        CancellationToken ct = default)
    {
        lock (this._statusLock)
        {
            if (!this._jobs.TryGetValue(id, out var job) || job.Status != expectedStatus)
            {
                return Task.FromResult<SupersededReviewJobState?>(null);
            }

            var previous = new SupersededReviewJobState(
                job.Status,
                job.CompletedAt,
                job.LeaseOwner,
                job.LeaseExpiresAt,
                job.LastHeartbeatAt);

            job.Status = JobStatus.Superseded;
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.ClearLease();
            return Task.FromResult<SupersededReviewJobState?>(previous);
        }
    }

    public Task<bool> TryRestoreSupersededAsync(
        Guid id,
        SupersededReviewJobState state,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        lock (this._statusLock)
        {
            if (!this._jobs.TryGetValue(id, out var job) || job.Status != JobStatus.Superseded)
            {
                return Task.FromResult(false);
            }

            job.Status = state.Status;
            job.CompletedAt = state.CompletedAt;
            if (state.LeaseOwner is { } owner
                && state.LeaseExpiresAt is { } expiresAt
                && state.LastHeartbeatAt is { } lastHeartbeatAt)
            {
                job.ApplyLease(owner, Math.Max(job.LeaseGeneration, 1), expiresAt, lastHeartbeatAt);
            }
            else
            {
                job.ClearLease();
            }

            return Task.FromResult(true);
        }
    }

    public Task SetStoppedAsync(Guid id, CancellationToken ct = default)
    {
        lock (this._statusLock)
        {
            if (this._jobs.TryGetValue(id, out var job) &&
                job.Status is not (JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Superseded or JobStatus.Stopped
                    or JobStatus.AdmissionRefused))
            {
                job.Status = JobStatus.Stopped;
                job.CompletedAt = DateTimeOffset.UtcNow;
            }
        }

        return Task.CompletedTask;
    }

    public Task SetBudgetExceededAsync(
        Guid id,
        BudgetScopeKind? scope,
        BudgetCapKind capKind,
        decimal? thresholdUsd,
        decimal? spentUsd,
        CancellationToken ct = default)
    {
        lock (this._statusLock)
        {
            if (this._jobs.TryGetValue(id, out var job) &&
                job.Status is not (JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Superseded or JobStatus.Stopped
                    or JobStatus.AdmissionRefused))
            {
                job.SetBudgetBlock(scope, capKind, thresholdUsd, spentUsd);
                job.Status = JobStatus.BudgetExceeded;
                job.CompletedAt = DateTimeOffset.UtcNow;
            }
        }

        return Task.CompletedTask;
    }

    public Task SetBudgetHeldAsync(
        Guid id,
        BudgetScopeKind scope,
        BudgetCapKind capKind,
        decimal thresholdUsd,
        decimal spentUsd,
        CancellationToken ct = default)
    {
        lock (this._statusLock)
        {
            if (this._jobs.TryGetValue(id, out var job) && job.Status == JobStatus.Pending)
            {
                job.SetBudgetBlock(scope, capKind, thresholdUsd, spentUsd);
                job.Status = JobStatus.BudgetHeld;
            }
        }

        return Task.CompletedTask;
    }

    public Task SetAdmissionHeldAsync(Guid id, DateTimeOffset heldUntil, CancellationToken ct = default)
    {
        lock (this._statusLock)
        {
            if (this._jobs.TryGetValue(id, out var job) && job.Status == JobStatus.Pending)
            {
                job.SetAdmissionHold(heldUntil);
                job.Status = JobStatus.AdmissionHeld;
            }
        }

        return Task.CompletedTask;
    }

    public Task<bool> SetAdmissionRefusedAsync(Guid id, string reason, string? policyFingerprint, CancellationToken ct = default)
    {
        lock (this._statusLock)
        {
            if (!this._jobs.TryGetValue(id, out var job)
                || job.Status is not (JobStatus.Pending or JobStatus.Processing or JobStatus.AdmissionHeld))
            {
                return Task.FromResult(false);
            }

            job.SetAdmissionRefusal(reason, policyFingerprint);
            job.Status = JobStatus.AdmissionRefused;
            job.CompletedAt = DateTimeOffset.UtcNow;
            return Task.FromResult(true);
        }
    }

    public Task<int> ReleaseDueAdmissionHoldsAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var released = 0;
        lock (this._statusLock)
        {
            foreach (var job in this._jobs.Values)
            {
                if (job.Status == JobStatus.AdmissionHeld && job.HeldUntil is { } heldUntil && heldUntil <= now)
                {
                    job.ClearAdmissionHold();
                    job.Status = JobStatus.Pending;
                    released++;
                }
            }
        }

        return Task.FromResult(released);
    }

    public Task<ReviewSubmissionWindow> GetSubmissionWindowAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        DateTimeOffset since,
        Guid excludeJobId,
        CancellationToken ct = default)
    {
        // Under the lock the status writers take, so the window is counted against a set of jobs no
        // transition is halfway through. The admission decision the caller makes from it is a second step:
        // one host evaluates its candidates one after another, so no other evaluator of the same pull
        // request is inside that gap.
        //
        // The bound is on reviews an AI performed, so a job counts once a model call was made for it: while it
        // is processing, and afterwards whatever status it reached, because its token aggregates then record
        // the calls. A job still queued, a job refused, a job still held and a job that ended before its first
        // model call cost nothing, so none of them takes a place in the hour. The relational store counts the
        // same jobs.
        List<DateTimeOffset> inWindow;
        lock (this._statusLock)
        {
            inWindow = this._jobs.Values
                .Where(job =>
                    job.ClientId == clientId
                    && string.Equals(job.OrganizationUrl, organizationUrl, StringComparison.Ordinal)
                    && string.Equals(job.ProjectId, projectId, StringComparison.Ordinal)
                    && string.Equals(job.RepositoryId, repositoryId, StringComparison.Ordinal)
                    && job.PullRequestId == pullRequestId
                    && job.SubmittedAt >= since
                    && job.Id != excludeJobId
                    && (job.Status == JobStatus.Processing
                        || job.TotalInputTokensAggregated > 0
                        || job.TotalOutputTokensAggregated > 0))
                .Select(job => job.SubmittedAt)
                .ToList();
        }

        return Task.FromResult(
            inWindow.Count == 0
                ? ReviewSubmissionWindow.Empty
                : new ReviewSubmissionWindow(inWindow.Count, inWindow.Min()));
    }

    public Task<IReadOnlyList<ReviewJob>> GetActiveJobsForConfigAsync(
        string organizationUrl,
        string projectId,
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ReviewJob>>(
            this._jobs.Values
                .Where(job =>
                    string.Equals(job.OrganizationUrl, organizationUrl, StringComparison.Ordinal)
                    && string.Equals(job.ProjectId, projectId, StringComparison.Ordinal)
                    && job.Status is JobStatus.Pending or JobStatus.Processing or JobStatus.BudgetHeld or JobStatus.BudgetExceeded or JobStatus.AdmissionHeld)
                .ToList()
                .AsReadOnly());
    }

    public Task<ReviewJob?> GetCompletedJobWithFileResultsAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        CancellationToken ct = default)
    {
        return Task.FromResult(this.FindCompletedJob(organizationUrl, projectId, repositoryId, pullRequestId, iterationId));
    }

    public Task<ReviewJob?> GetCompletedJobWithFileResultsByStoredRevisionAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        string storedRevisionKey,
        CancellationToken ct = default)
    {
        return Task.FromResult(
            this._jobs.Values
                .Where(job =>
                    string.Equals(job.OrganizationUrl, organizationUrl, StringComparison.Ordinal)
                    && string.Equals(job.ProjectId, projectId, StringComparison.Ordinal)
                    && string.Equals(job.RepositoryId, repositoryId, StringComparison.Ordinal)
                    && job.PullRequestId == pullRequestId
                    && job.Status == JobStatus.Completed
                    && string.Equals(
                        ReviewRevisionKeys.GetStoredKey(job.ReviewRevisionReference, job.IterationId),
                        storedRevisionKey,
                        StringComparison.Ordinal))
                .OrderByDescending(job => job.CompletedAt)
                .FirstOrDefault());
    }

    public Task<ReviewJob?> GetLatestReusableTerminalJobAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid excludeJobId,
        string currentRevisionKey,
        CancellationToken ct = default)
    {
        var candidates = this._jobs.Values
            .Where(job =>
                string.Equals(job.OrganizationUrl, organizationUrl, StringComparison.Ordinal)
                && string.Equals(job.ProjectId, projectId, StringComparison.Ordinal)
                && string.Equals(job.RepositoryId, repositoryId, StringComparison.Ordinal)
                && job.PullRequestId == pullRequestId
                && job.Id != excludeJobId
                && job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Superseded);

        return Task.FromResult(ReviewBaselineSelection.SelectReusableBaseline(candidates, currentRevisionKey));
    }

    public Task<EngagedReviewRevision?> GetLatestEngagedRevisionAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct = default)
    {
        // A job blocked at a budget cap is waiting on a manual restart, so it is not an engagement with its
        // revision; every other status is, including a review still in flight.
        var latest = this._jobs.Values
            .Where(job =>
                job.ClientId == clientId
                && string.Equals(job.OrganizationUrl, organizationUrl, StringComparison.Ordinal)
                && string.Equals(job.ProjectId, projectId, StringComparison.Ordinal)
                && string.Equals(job.RepositoryId, repositoryId, StringComparison.Ordinal)
                && job.PullRequestId == pullRequestId
                && job.Status is not (JobStatus.BudgetHeld or JobStatus.BudgetExceeded))
            .OrderByDescending(job => job.SubmittedAt)
            .ThenByDescending(job => job.IterationId)
            .FirstOrDefault();

        return Task.FromResult(
            latest is null
                ? null
                : new EngagedReviewRevision(
                    ReviewRevisionKeys.GetStoredKey(latest.ReviewRevisionReference, latest.IterationId),
                    latest.ReviewRevisionReference,
                    latest.IterationId));
    }

    public Task<RefusedReviewAdmission?> GetLatestRefusedAdmissionAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct = default)
    {
        var latest = this._jobs.Values
            .Where(job =>
                job.ClientId == clientId
                && string.Equals(job.OrganizationUrl, organizationUrl, StringComparison.Ordinal)
                && string.Equals(job.ProjectId, projectId, StringComparison.Ordinal)
                && string.Equals(job.RepositoryId, repositoryId, StringComparison.Ordinal)
                && job.PullRequestId == pullRequestId
                && job.Status == JobStatus.AdmissionRefused)
            .OrderByDescending(job => job.SubmittedAt)
            .ThenByDescending(job => job.IterationId)
            .FirstOrDefault();

        return Task.FromResult(
            latest is null
                ? null
                : new RefusedReviewAdmission(
                    ReviewRevisionKeys.GetStoredKey(latest.ReviewRevisionReference, latest.IterationId),
                    latest.AdmissionPolicyFingerprint));
    }

    public Task<ReviewJob?> GetBestTerminalJobWithFileResultsByStoredRevisionAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        string storedRevisionKey,
        CancellationToken ct = default)
    {
        return Task.FromResult(
            this._jobs.Values
                .Where(job =>
                    string.Equals(job.OrganizationUrl, organizationUrl, StringComparison.Ordinal)
                    && string.Equals(job.ProjectId, projectId, StringComparison.Ordinal)
                    && string.Equals(job.RepositoryId, repositoryId, StringComparison.Ordinal)
                    && job.PullRequestId == pullRequestId
                    && job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Superseded
                    && string.Equals(
                        ReviewRevisionKeys.GetStoredKey(job.ReviewRevisionReference, job.IterationId),
                        storedRevisionKey,
                        StringComparison.Ordinal))
                .OrderByDescending(ReviewBaselineSelection.CountUsableReviewedResults)
                .ThenByDescending(job => job.CompletedAt)
                .FirstOrDefault());
    }

    public Task UpdateAiConfigAsync(
        Guid id,
        Guid? connectionId,
        string? model,
        CancellationToken ct = default,
        float? reviewTemperature = null)
    {
        if (this._jobs.TryGetValue(id, out var job))
        {
            job.SetAiConfig(connectionId, model, reviewTemperature);
        }

        return Task.CompletedTask;
    }

    public Task UpdatePrContextAsync(
        Guid id,
        string? prTitle,
        string? prRepositoryName,
        string? prSourceBranch,
        string? prTargetBranch,
        CancellationToken ct = default)
    {
        if (this._jobs.TryGetValue(id, out var job))
        {
            job.SetPrContext(prTitle, prRepositoryName, prSourceBranch, prTargetBranch);
        }

        return Task.CompletedTask;
    }

    public Task UpdatePullRequestAuthorAsync(Guid id, PullRequestAuthor author, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(author);

        if (this._jobs.TryGetValue(id, out var job))
        {
            job.SetPullRequestAuthor(author);
        }

        return Task.CompletedTask;
    }

    public Task<string?> FindRecordedRepositoryIdAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryName,
        int pullRequestId,
        CancellationToken ct = default)
    {
        var candidates = this._jobs.Values
            .Where(job =>
                job.ClientId == clientId
                && string.Equals(job.OrganizationUrl, organizationUrl, StringComparison.Ordinal)
                && string.Equals(job.ProjectId, projectId, StringComparison.Ordinal)
                && job.PullRequestId == pullRequestId)
            .ToList();

        // A pull request number is unique per repository on GitLab and Forgejo, so the name recorded with
        // the job is what settles which repository in this project is meant.
        var named = candidates
            .Where(job => string.Equals(job.PrRepositoryName, repositoryName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var pool = named.Count > 0 ? named : candidates;
        var identities = pool
            .Select(job => job.RepositoryId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult(identities.Count == 1 ? identities[0] : null);
    }

    public Task<IReadOnlyList<ReviewJob>> GetByPrAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var items = this._jobs.Values
            .Where(job =>
                job.ClientId == clientId
                && string.Equals(job.OrganizationUrl, organizationUrl, StringComparison.Ordinal)
                && string.Equals(job.ProjectId, projectId, StringComparison.Ordinal)
                && string.Equals(job.RepositoryId, repositoryId, StringComparison.Ordinal)
                && job.PullRequestId == pullRequestId)
            .OrderByDescending(job => job.SubmittedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList()
            .AsReadOnly();

        return Task.FromResult<IReadOnlyList<ReviewJob>>(items);
    }

    private static bool RepositoryMatches(ReviewJob job, string repositoryId, string projectId)
    {
        return string.Equals(
            GetRepositoryIdentityKey(job, job.RepositoryId, projectId),
            GetRepositoryIdentityKey(job, repositoryId, projectId),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRepositoryIdentityKey(ReviewJob job, string repositoryId, string projectId)
    {
        if (job.Provider == ScmProvider.AzureDevOps)
        {
            return repositoryId;
        }

        var projectPath = string.IsNullOrWhiteSpace(job.RepositoryProjectPath)
            ? repositoryId
            : job.RepositoryProjectPath;
        if (LooksLikeRepositoryPath(repositoryId) || LooksLikeRepositoryPath(projectPath))
        {
            return projectPath;
        }

        var ownerOrNamespace = string.IsNullOrWhiteSpace(job.RepositoryOwnerOrNamespace)
            ? projectId
            : job.RepositoryOwnerOrNamespace;
        return string.Equals(repositoryId, job.RepositoryId, StringComparison.OrdinalIgnoreCase)
            ? $"{ownerOrNamespace}/{repositoryId}"
            : repositoryId;
    }

    private static bool LooksLikeRepositoryPath(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && value.Contains('/', StringComparison.Ordinal);
    }
}
