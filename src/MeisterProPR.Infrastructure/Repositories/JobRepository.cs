// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Support;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>Implementation of <see cref="IJobRepository" /> using EF Core.</summary>
public sealed partial class JobRepository(
    MeisterProPRDbContext dbContext,
    IDbContextFactory<MeisterProPRDbContext> contextFactory,
    ILogger<JobRepository> logger) : IJobRepository
{
    /// <inheritdoc />
    public async Task<bool> TryTransitionAsync(Guid id, JobStatus from, JobStatus to, CancellationToken ct = default)
    {
        var job = await dbContext.ReviewJobs.FindAsync([id], ct);
        if (job is null || job.Status != from)
        {
            return false;
        }

        job.Status = to;
        if (to == JobStatus.Processing)
        {
            job.ProcessingStartedAt = DateTimeOffset.UtcNow;
        }

        try
        {
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            if (from == JobStatus.Processing && to == JobStatus.Pending)
            {
                // Recovery reset: the job is leaving Processing, so any still-open protocol
                // rows are abandoned in-flight passes that will never be finalized.
                await this.CloseOpenProtocolsAsync(id).ConfigureAwait(false);
            }

            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            await dbContext.Entry(job).ReloadAsync(ct).ConfigureAwait(false);
            return false;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ReviewJob> GetAllForClient(Guid clientId)
    {
        var jobs = dbContext.ReviewJobs
            .Where(j => j.ClientId == clientId)
            .OrderByDescending(j => j.SubmittedAt)
            .ToList();

        this.HydrateSourceScopes(jobs);
        return jobs;
    }

    /// <inheritdoc />
    public IReadOnlyList<ReviewJob> GetPendingJobs()
    {
        var jobs = dbContext.ReviewJobs
            .Where(j => j.Status == JobStatus.Pending)
            .OrderBy(j => j.SubmittedAt)
            .ToList();

        this.HydrateSourceScopes(jobs);
        return jobs;
    }

    /// <inheritdoc />
    public ReviewJob? FindActiveJob(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId)
    {
        return dbContext.ReviewJobs
            .AsEnumerable()
            .FirstOrDefault(j => string.Equals(j.OrganizationUrl, organizationUrl, StringComparison.Ordinal)
                                 && string.Equals(j.ProjectId, projectId, StringComparison.Ordinal)
                                 && RepositoryMatches(j, repositoryId, projectId)
                                 && j.PullRequestId == pullRequestId
                                 && j.IterationId == iterationId
                                 && (j.Status == JobStatus.Pending || j.Status == JobStatus.Processing));
    }

    /// <inheritdoc />
    public ReviewJob? FindCompletedJob(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId)
    {
        return dbContext.ReviewJobs
            .AsEnumerable()
            .Where(j => string.Equals(j.OrganizationUrl, organizationUrl, StringComparison.Ordinal)
                        && string.Equals(j.ProjectId, projectId, StringComparison.Ordinal)
                        && RepositoryMatches(j, repositoryId, projectId)
                        && j.PullRequestId == pullRequestId
                        && j.IterationId == iterationId
                        && j.Status == JobStatus.Completed)
            .OrderByDescending(j => j.CompletedAt)
            .FirstOrDefault();
    }

    /// <inheritdoc />
    public ReviewJob? FindFailedJob(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId)
    {
        return dbContext.ReviewJobs
            .AsEnumerable()
            .Where(j => string.Equals(j.OrganizationUrl, organizationUrl, StringComparison.Ordinal)
                        && string.Equals(j.ProjectId, projectId, StringComparison.Ordinal)
                        && RepositoryMatches(j, repositoryId, projectId)
                        && j.PullRequestId == pullRequestId
                        && j.IterationId == iterationId
                        && j.Status == JobStatus.Failed)
            .OrderByDescending(j => j.CompletedAt)
            .FirstOrDefault();
    }

    /// <inheritdoc />
    public ReviewJob? GetById(Guid id)
    {
        var job = dbContext.ReviewJobs.Find(id);
        if (job is not null)
        {
            this.HydrateSourceScope(job);
        }

        return job;
    }

    /// <inheritdoc />
    public async Task<(int total, IReadOnlyList<ReviewJob> items)> GetAllJobsAsync(
        int limit,
        int offset,
        JobStatus? status,
        Guid? clientId = null,
        int? pullRequestId = null,
        CancellationToken ct = default)
    {
        var query = dbContext.ReviewJobs.AsQueryable();
        if (status.HasValue)
        {
            query = query.Where(j => j.Status == status.Value);
        }

        if (clientId.HasValue)
        {
            query = query.Where(j => j.ClientId == clientId.Value);
        }

        if (pullRequestId.HasValue)
        {
            query = query.Where(j => j.PullRequestId == pullRequestId.Value);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .Include(j => j.Protocols)
            .OrderByDescending(j => j.SubmittedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        await this.HydrateSourceScopesAsync(items, ct);

        return (total, items);
    }

    /// <inheritdoc />
    public async Task<(int total, IReadOnlyList<JobListPageItemDto> items)> GetJobListPageAsync(
        int limit,
        int offset,
        JobStatus? status,
        Guid? clientId = null,
        int? pullRequestId = null,
        CancellationToken ct = default)
    {
        var query = dbContext.ReviewJobs.AsNoTracking();
        if (status.HasValue)
        {
            query = query.Where(j => j.Status == status.Value);
        }

        if (clientId.HasValue)
        {
            query = query.Where(j => j.ClientId == clientId.Value);
        }

        if (pullRequestId.HasValue)
        {
            query = query.Where(j => j.PullRequestId == pullRequestId.Value);
        }

        var total = await query.CountAsync(ct).ConfigureAwait(false);

        // Project to a non-entity DTO: selects only scalar columns (the summary from result_summary, never
        // result_json), sums protocol tokens as a correlated subquery coalesced to 0, and is untracked by
        // construction. No source-scope hydration — the overview never renders it.
        var items = await query
            .OrderByDescending(j => j.SubmittedAt)
            .Skip(offset)
            .Take(limit)
            .Select(j => new JobListPageItemDto(
                j.Id,
                j.ClientId,
                j.OrganizationUrl,
                j.ProjectId,
                j.RepositoryId,
                j.PullRequestId,
                j.IterationId,
                j.Status,
                j.SubmittedAt,
                j.ProcessingStartedAt,
                j.CompletedAt,
                j.ResultSummary,
                j.ErrorMessage,
                j.TotalInputTokensAggregated ?? j.Protocols.Sum(p => p.TotalInputTokens) ?? 0L,
                j.TotalOutputTokensAggregated ?? j.Protocols.Sum(p => p.TotalOutputTokens) ?? 0L,
                j.PrTitle,
                j.PrSourceBranch,
                j.PrTargetBranch,
                j.PrRepositoryName,
                j.AiModel,
                // Live numerator: files whose review reached a terminal successful state. Correlated Count
                // over boolean columns only — no file-result text is materialized. Mirrors the "reviewed"
                // predicate reused across this repository (excludes excluded, failed, and carried-forward).
                j.FileReviewResults.Count(r => r.IsComplete && !r.IsFailed && !r.IsExcluded && !r.IsCarriedForward),
                j.InScopeChangedFileCount))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return (total, items);
    }

    /// <inheritdoc />
    public async Task UpdateInScopeChangedFileCountAsync(Guid id, int count, CancellationToken ct = default)
    {
        var job = await dbContext.ReviewJobs.FindAsync([id], ct).ConfigureAwait(false);
        if (job is null)
        {
            return;
        }

        job.SetInScopeChangedFileCount(count);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<int> CountReviewedFilesAsync(Guid jobId, CancellationToken ct = default)
    {
        // Index-backed COUNT over review_file_results boolean columns (ix_review_file_results_job_id):
        // projection-only, loads no file-result text. Same "reviewed" predicate as the list projection.
        return dbContext.ReviewFileResults
            .Where(r => r.JobId == jobId && r.IsComplete && !r.IsFailed && !r.IsExcluded && !r.IsCarriedForward)
            .CountAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReviewJob>> GetProcessingJobsAsync(CancellationToken ct = default)
    {
        var jobs = await dbContext.ReviewJobs
            .Where(j => j.Status == JobStatus.Processing)
            .ToListAsync(ct);

        await this.HydrateSourceScopesAsync(jobs, ct);
        return jobs;
    }

    /// <inheritdoc />
    public Task<int> CountProcessingJobsAsync(CancellationToken ct = default)
    {
        return dbContext.ReviewJobs.CountAsync(j => j.Status == JobStatus.Processing, ct);
    }

    /// <inheritdoc />
    public async Task AddAsync(ReviewJob job, CancellationToken ct = default)
    {
        dbContext.ReviewJobs.Add(job);
        await this.PersistJobSourceScopeAsync(job, ct);
        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<TryAddReviewJobResult> TryAddIfNoActiveDuplicateAsync(ReviewJob job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(ct);
        await this.AcquireReviewJobLockAsync(job, ct);

        var activeJobs = await dbContext.ReviewJobs
            .Where(candidate => candidate.OrganizationUrl == job.OrganizationUrl
                                && candidate.ProjectId == job.ProjectId
                                && candidate.PullRequestId == job.PullRequestId
                                && (candidate.Status == JobStatus.Pending || candidate.Status == JobStatus.Processing))
            .ToListAsync(ct);
        activeJobs = activeJobs
            .Where(candidate => RepositoryMatches(candidate, job.RepositoryId, job.ProjectId))
            .ToList();

        var currentRevisionKey = ReviewRevisionKeys.TryGetStoredKey(job.ReviewRevisionReference);
        if (!string.IsNullOrWhiteSpace(currentRevisionKey))
        {
            var duplicateJob = activeJobs.FirstOrDefault(candidate => string.Equals(
                ReviewRevisionKeys.GetStoredKey(candidate.ReviewRevisionReference, candidate.IterationId),
                currentRevisionKey,
                StringComparison.Ordinal));
            if (duplicateJob is not null)
            {
                await transaction.CommitAsync(ct);
                return new TryAddReviewJobResult(false, duplicateJob, 0);
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

            dbContext.ReviewJobs.Add(job);
            await this.PersistJobSourceScopeAsync(job, ct);
            await dbContext.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new TryAddReviewJobResult(true, null, cancelledSupersededJobCount);
        }

        var duplicateIterationJob = activeJobs.FirstOrDefault(candidate => candidate.IterationId == job.IterationId);
        if (duplicateIterationJob is not null)
        {
            await transaction.CommitAsync(ct);
            return new TryAddReviewJobResult(false, duplicateIterationJob, 0);
        }

        dbContext.ReviewJobs.Add(job);
        await this.PersistJobSourceScopeAsync(job, ct);
        await dbContext.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new TryAddReviewJobResult(true, null, 0);
    }

    /// <inheritdoc />
    public async Task UpdateRetryCountAsync(Guid id, int retryCount, CancellationToken ct = default)
    {
        var job = await dbContext.ReviewJobs.FindAsync([id], ct);
        if (job is null)
        {
            return;
        }

        job.RetryCount = retryCount;
        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task SetFailedAsync(Guid id, string errorMessage, CancellationToken ct = default)
    {
        var job = await dbContext.ReviewJobs.FindAsync([id], ct);
        if (job is null)
        {
            return;
        }

        job.ErrorMessage = errorMessage;
        job.Status = JobStatus.Failed;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        await this.CloseOpenProtocolsAsync(id).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var job = await dbContext.ReviewJobs.FindAsync([id], ct);
        if (job is null)
        {
            return;
        }

        dbContext.ReviewJobs.Remove(job);
        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task SetResultAsync(Guid id, ReviewResult result, CancellationToken ct = default)
    {
        var job = await dbContext.ReviewJobs.FindAsync([id], ct);
        if (job is null)
        {
            return;
        }

        job.ApplyResult(result);
        job.Status = JobStatus.Completed;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        await this.CloseOpenProtocolsAsync(id).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ReviewJob?> GetByIdWithFileResultsAsync(Guid id, CancellationToken ct = default)
    {
        var job = await dbContext.ReviewJobs
            .Include(j => j.FileReviewResults)
            .FirstOrDefaultAsync(j => j.Id == id, ct);

        if (job is not null)
        {
            await this.HydrateSourceScopeAsync(job, ct);
        }

        return job;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Uses a short-lived <see cref="MeisterProPRDbContext" /> from the factory so concurrent
    ///     calls from parallel file-review tasks cannot share the same context instance.
    /// </remarks>
    public async Task AddFileResultAsync(ReviewFileResult result, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        db.ReviewFileResults.Add(result);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Uses a short-lived <see cref="MeisterProPRDbContext" /> from the factory so concurrent
    ///     calls from parallel file-review tasks cannot share the same context instance.
    /// </remarks>
    public async Task UpdateFileResultAsync(ReviewFileResult result, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        db.ReviewFileResults.Update(result);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<ReviewJob?> GetByIdWithProtocolsAsync(Guid id, CancellationToken ct = default)
    {
        // Read-only path: every caller (the full per-pass trace reader and its inherited-source
        // fallbacks) only projects the loaded graph into DTOs and never persists it. Skipping change
        // tracking roughly halves the managed-heap footprint of a heavy trace (which carries the full
        // event bodies and phase_timings jsonb).
        var job = await dbContext.ReviewJobs
            .AsNoTracking()
            .AsSplitQuery()
            .Include(j => j.Protocols.OrderByDescending(p => p.AttemptNumber))
            .ThenInclude(p => p.Events.OrderBy(e => e.OccurredAt))
            .Include(j => j.FileReviewResults)
            .FirstOrDefaultAsync(j => j.Id == id, ct);

        if (job is not null)
        {
            await this.HydrateSourceScopeAsync(job, ct);
        }

        return job;
    }

    /// <inheritdoc />
    public async Task<ReviewJob?> GetByIdWithProtocolsForOverviewAsync(Guid id, CancellationToken ct = default)
    {
        // Query 1: job + protocols (ordered) + file results, WITHOUT events. Loading events here would
        // pull every column including the heavy phase_timings jsonb; events are attached separately below
        // via a projection that excludes that column.
        var job = await dbContext.ReviewJobs
            .AsNoTracking()
            .AsSplitQuery()
            .Include(j => j.Protocols.OrderByDescending(p => p.AttemptNumber))
            .Include(j => j.FileReviewResults)
            .FirstOrDefaultAsync(j => j.Id == id, ct);

        if (job is null)
        {
            return null;
        }

        // Query 2: load every event column EXCEPT phase_timings. The text columns (InputTextSample,
        // SystemPrompt, OutputSummary) are kept because the overview's Resolve* pass-badge helpers parse them.
        var protocolIds = job.Protocols.Select(p => p.Id).ToList();
        var events = await dbContext.ProtocolEvents
            .AsNoTracking()
            .Where(e => protocolIds.Contains(e.ProtocolId))
            .OrderBy(e => e.OccurredAt)
            .Select(e => new ProtocolEvent
            {
                Id = e.Id,
                ProtocolId = e.ProtocolId,
                Kind = e.Kind,
                Name = e.Name,
                OccurredAt = e.OccurredAt,
                InputTokens = e.InputTokens,
                OutputTokens = e.OutputTokens,
                CachedInputTokens = e.CachedInputTokens,
                CacheStatus = e.CacheStatus,
                CacheMissCategory = e.CacheMissCategory,
                PrefixEligibility = e.PrefixEligibility,
                ToolEvidenceAction = e.ToolEvidenceAction,
                ToolEvidenceSourceToolName = e.ToolEvidenceSourceToolName,
                ToolEvidenceOriginalPayloadTokens = e.ToolEvidenceOriginalPayloadTokens,
                ToolEvidenceBoundedPayloadTokens = e.ToolEvidenceBoundedPayloadTokens,
                ToolEvidenceRefreshable = e.ToolEvidenceRefreshable,
                FinalizationAttemptKind = e.FinalizationAttemptKind,
                FinalizationReason = e.FinalizationReason,
                FinalizationOutcome = e.FinalizationOutcome,
                InputTextSample = e.InputTextSample,
                StartedAt = e.StartedAt,
                CompletedAt = e.CompletedAt,
                DurationMs = e.DurationMs,
                WaitDurationMs = e.WaitDurationMs,
                ActiveDurationMs = e.ActiveDurationMs,
                TimingAvailability = e.TimingAvailability,
                ToolOutcome = e.ToolOutcome,
                SystemPrompt = e.SystemPrompt,
                OutputSummary = e.OutputSummary,
                EventCategory = e.EventCategory,
                Error = e.Error,

                // PhaseTimings intentionally NOT selected: it is neither serialized nor read on the overview
                // path and dominates the load on heavy traces.
            })
            .ToListAsync(ct);

        var eventsByProtocol = events.GroupBy(e => e.ProtocolId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var protocol in job.Protocols.Where(protocol => eventsByProtocol.ContainsKey(protocol.Id)))
        {
            foreach (var evt in eventsByProtocol[protocol.Id])
            {
                protocol.Events.Add(evt);
            }
        }

        await this.HydrateSourceScopeAsync(job, ct);

        return job;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReviewJob>> GetStuckProcessingJobsAsync(
        TimeSpan threshold,
        CancellationToken ct = default)
    {
        var staleBeforeUtc = DateTimeOffset.UtcNow - threshold;
        var jobs = await dbContext.ReviewJobs
            .Where(j => j.Status == JobStatus.Processing && j.ProcessingStartedAt < staleBeforeUtc)
            .ToListAsync(ct);

        await this.HydrateSourceScopesAsync(jobs, ct);
        return jobs;
    }

    /// <inheritdoc />
    [Obsolete("Use GetByIdWithProtocolsAsync instead.")]
    public async Task<ReviewJob?> GetByIdWithProtocolAsync(Guid id, CancellationToken ct = default)
    {
        return await this.GetByIdWithProtocolsAsync(id, ct);
    }

    /// <inheritdoc />
    public async Task SetCancelledAsync(Guid id, CancellationToken ct = default)
    {
        var job = await dbContext.ReviewJobs.FindAsync([id], ct);
        if (job is null || job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Superseded)
        {
            return;
        }

        job.Status = JobStatus.Cancelled;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        await this.CloseOpenProtocolsAsync(id).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SetSupersededAsync(Guid id, CancellationToken ct = default)
    {
        var job = await dbContext.ReviewJobs.FindAsync([id], ct);
        if (job is null || job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Superseded)
        {
            return;
        }

        job.Status = JobStatus.Superseded;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        await this.CloseOpenProtocolsAsync(id).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReviewJob>> GetActiveJobsForConfigAsync(
        string organizationUrl,
        string projectId,
        CancellationToken ct = default)
    {
        var jobs = await dbContext.ReviewJobs
            .Where(j => j.OrganizationUrl == organizationUrl &&
                        j.ProjectId == projectId &&
                        (j.Status == JobStatus.Pending || j.Status == JobStatus.Processing))
            .ToListAsync(ct);

        await this.HydrateSourceScopesAsync(jobs, ct);
        return jobs;
    }

    /// <inheritdoc />
    public async Task<ReviewJob?> GetCompletedJobWithFileResultsAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        CancellationToken ct = default)
    {
        var job = await dbContext.ReviewJobs
            .Include(j => j.FileReviewResults)
            .Where(j => j.OrganizationUrl == organizationUrl &&
                        j.ProjectId == projectId &&
                        j.RepositoryId == repositoryId &&
                        j.PullRequestId == pullRequestId &&
                        j.IterationId == iterationId &&
                        j.Status == JobStatus.Completed)
            .OrderByDescending(j => j.CompletedAt)
            .FirstOrDefaultAsync(ct);

        if (job is not null)
        {
            await this.HydrateSourceScopeAsync(job, ct);
        }

        return job;
    }

    /// <inheritdoc />
    public async Task<ReviewJob?> GetCompletedJobWithFileResultsByStoredRevisionAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        string storedRevisionKey,
        CancellationToken ct = default)
    {
        var matchingJobs = await dbContext.ReviewJobs
            .Include(j => j.FileReviewResults)
            .Where(j => j.OrganizationUrl == organizationUrl &&
                        j.ProjectId == projectId &&
                        j.RepositoryId == repositoryId &&
                        j.PullRequestId == pullRequestId &&
                        j.Status == JobStatus.Completed)
            .OrderByDescending(j => j.CompletedAt)
            .ToListAsync(ct);

        var job = matchingJobs.FirstOrDefault(j => string.Equals(
            ReviewRevisionKeys.GetStoredKey(j.ReviewRevisionReference, j.IterationId),
            storedRevisionKey,
            StringComparison.Ordinal));

        if (job is not null)
        {
            await this.HydrateSourceScopeAsync(job, ct);
        }

        return job;
    }

    /// <inheritdoc />
    public async Task<ReviewJob?> GetLatestReusableTerminalJobAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid excludeJobId,
        string currentRevisionKey,
        CancellationToken ct = default)
    {
        var candidateJobs = await dbContext.ReviewJobs
            .Include(j => j.FileReviewResults)
            .Where(j => j.OrganizationUrl == organizationUrl &&
                        j.ProjectId == projectId &&
                        j.RepositoryId == repositoryId &&
                        j.PullRequestId == pullRequestId &&
                        j.Id != excludeJobId &&
                        (j.Status == JobStatus.Completed ||
                         j.Status == JobStatus.Failed ||
                         j.Status == JobStatus.Cancelled ||
                         j.Status == JobStatus.Superseded))
            .ToListAsync(ct);

        var job = ReviewBaselineSelection.SelectReusableBaseline(candidateJobs, currentRevisionKey);

        if (job is not null)
        {
            await this.HydrateSourceScopeAsync(job, ct);
        }

        return job;
    }

    /// <inheritdoc />
    public async Task<ReviewJob?> GetBestTerminalJobWithFileResultsByStoredRevisionAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        string storedRevisionKey,
        CancellationToken ct = default)
    {
        var matchingJobs = await dbContext.ReviewJobs
            .Include(j => j.FileReviewResults)
            .Where(j => j.OrganizationUrl == organizationUrl &&
                        j.ProjectId == projectId &&
                        j.RepositoryId == repositoryId &&
                        j.PullRequestId == pullRequestId &&
                        (j.Status == JobStatus.Completed ||
                         j.Status == JobStatus.Failed ||
                         j.Status == JobStatus.Cancelled ||
                         j.Status == JobStatus.Superseded))
            .ToListAsync(ct);

        var job = matchingJobs
            .Where(j => string.Equals(
                ReviewRevisionKeys.GetStoredKey(j.ReviewRevisionReference, j.IterationId),
                storedRevisionKey,
                StringComparison.Ordinal))
            .OrderByDescending(ReviewBaselineSelection.CountUsableReviewedResults)
            .ThenByDescending(j => j.CompletedAt)
            .FirstOrDefault();

        if (job is not null)
        {
            await this.HydrateSourceScopeAsync(job, ct);
        }

        return job;
    }

    /// <inheritdoc />
    public async Task UpdateAiConfigAsync(
        Guid id,
        Guid? connectionId,
        string? model,
        CancellationToken ct = default,
        float? reviewTemperature = null)
    {
        var job = await dbContext.ReviewJobs.FindAsync([id], ct);
        if (job is null)
        {
            return;
        }

        job.SetAiConfig(connectionId, model, reviewTemperature);
        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task UpdatePrContextAsync(
        Guid id,
        string? prTitle,
        string? prRepositoryName,
        string? prSourceBranch,
        string? prTargetBranch,
        CancellationToken ct = default)
    {
        var job = await dbContext.ReviewJobs.FindAsync([id], ct);
        if (job is null)
        {
            return;
        }

        job.SetPrContext(prTitle, prRepositoryName, prSourceBranch, prTargetBranch);
        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReviewJob>> GetByPrAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var skip = (page - 1) * pageSize;
        var jobs = await dbContext.ReviewJobs
            .Include(j => j.Protocols.OrderByDescending(p => p.AttemptNumber))
            .ThenInclude(p => p.Events.OrderBy(e => e.OccurredAt))
            .Where(j => j.ClientId == clientId &&
                        j.OrganizationUrl == organizationUrl &&
                        j.ProjectId == projectId &&
                        j.RepositoryId == repositoryId &&
                        j.PullRequestId == pullRequestId)
            .OrderByDescending(j => j.SubmittedAt)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(ct);

        await this.HydrateSourceScopesAsync(jobs, ct);
        return jobs;
    }

    private void HydrateSourceScope(ReviewJob job)
    {
        this.HydrateSourceScopes([job]);
    }

    private async Task PersistJobSourceScopeAsync(ReviewJob job, CancellationToken ct)
    {
        if (job.ProCursorSourceScopeMode != ProCursorSourceScopeMode.SelectedSources)
        {
            return;
        }

        foreach (var sourceId in job.ProCursorSourceIds)
        {
            await dbContext.ReviewJobProCursorSourceScopes.AddAsync(
                new ReviewJobProCursorSourceScopeRecord
                {
                    ReviewJobId = job.Id,
                    ProCursorSourceId = sourceId,
                    CreatedAt = DateTimeOffset.UtcNow,
                },
                ct);
        }
    }

    private async Task AcquireReviewJobLockAsync(ReviewJob job, CancellationToken ct)
    {
        var lockRepositoryId = GetRepositoryIdentityKey(job, job.RepositoryId, job.ProjectId);
        var lockKey = $"{job.OrganizationUrl}\n{job.ProjectId}\n{lockRepositoryId}\n{job.PullRequestId}";
        await dbContext.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtext({0}))",
            new object[] { lockKey },
            ct);
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

    private async Task HydrateSourceScopeAsync(ReviewJob job, CancellationToken ct)
    {
        await this.HydrateSourceScopesAsync([job], ct);
    }

    private void HydrateSourceScopes(IReadOnlyList<ReviewJob> jobs)
    {
        if (jobs.Count == 0)
        {
            return;
        }

        var sourceIdsByJob = dbContext.ReviewJobProCursorSourceScopes
            .Where(scope => jobs.Select(job => job.Id).Contains(scope.ReviewJobId))
            .OrderBy(scope => scope.CreatedAt)
            .AsNoTracking()
            .AsEnumerable()
            .GroupBy(scope => scope.ReviewJobId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<Guid>)group.Select(scope => scope.ProCursorSourceId).ToList().AsReadOnly());

        foreach (var job in jobs)
        {
            job.SetProCursorSourceScope(
                job.ProCursorSourceScopeMode,
                sourceIdsByJob.TryGetValue(job.Id, out var sourceIds) ? sourceIds : []);
        }
    }

    private async Task HydrateSourceScopesAsync(IReadOnlyList<ReviewJob> jobs, CancellationToken ct)
    {
        if (jobs.Count == 0)
        {
            return;
        }

        var jobIds = jobs.Select(job => job.Id).ToArray();
        var sourceIdsByJob = await dbContext.ReviewJobProCursorSourceScopes
            .Where(scope => jobIds.Contains(scope.ReviewJobId))
            .OrderBy(scope => scope.CreatedAt)
            .AsNoTracking()
            .ToListAsync(ct);

        var groupedSourceIds = sourceIdsByJob
            .GroupBy(scope => scope.ReviewJobId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<Guid>)group.Select(scope => scope.ProCursorSourceId).ToList().AsReadOnly());

        foreach (var job in jobs)
        {
            job.SetProCursorSourceScope(
                job.ProCursorSourceScopeMode,
                groupedSourceIds.TryGetValue(job.Id, out var sourceIds) ? sourceIds : []);
        }
    }

    /// <summary>
    ///     Closes any still-open <see cref="ReviewJobProtocol" /> rows for the job (a maintenance/reconcile
    ///     operation), stamping <c>CompletedAt</c> and marking them <c>"Abandoned"</c>. Invoked whenever a job
    ///     leaves <see cref="JobStatus.Processing" /> — to <see cref="JobStatus.Pending" /> on recovery reset,
    ///     or to a terminal state — so abandoned in-flight passes never dangle. Queries
    ///     <see cref="MeisterProPRDbContext.ReviewJobProtocols" /> directly (rather than through the
    ///     <c>ReviewJob</c> aggregate) because this is a bulk set-based update over the aggregate child that must
    ///     bypass the change tracker to close rows committed by the factory-based recorder context.
    /// </summary>
    private async Task CloseOpenProtocolsAsync(Guid jobId)
    {
        // Best-effort cleanup: invoked after the job's own state change has already committed, from
        // cancel/shutdown and recovery paths. It must (a) run to completion even when the ambient
        // token is already cancelled — hence CancellationToken.None — and (b) never throw, so a
        // reconcile failure cannot abort the state transition or the startup-recovery loop. Any rows
        // missed here are closed by the next terminal or reset pass.
        try
        {
            var closed = await dbContext.ReviewJobProtocols
                .Where(p => p.JobId == jobId && p.CompletedAt == null)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(p => p.CompletedAt, DateTimeOffset.UtcNow)
                        .SetProperty(p => p.Outcome, "Abandoned"),
                    CancellationToken.None)
                .ConfigureAwait(false);

            if (closed > 0)
            {
                this.LogClosedOrphanedProtocols(closed, jobId);
            }
        }
        catch (Exception exception)
        {
            this.LogCloseOrphanedProtocolsFailed(exception, jobId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Closed {Count} orphaned protocol(s) for job {JobId}.")]
    private partial void LogClosedOrphanedProtocols(int count, Guid jobId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to close orphaned protocols for job {JobId}; the next terminal or reset pass will retry.")]
    private partial void LogCloseOrphanedProtocolsFailed(Exception exception, Guid jobId);
}
