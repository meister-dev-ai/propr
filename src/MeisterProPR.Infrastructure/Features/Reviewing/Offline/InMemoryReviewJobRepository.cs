// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Support;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     In-memory <see cref="IJobRepository" /> used by offline review execution.
/// </summary>
public sealed class InMemoryReviewJobRepository : IJobRepository
{
    private readonly ConcurrentDictionary<Guid, ReviewJob> _jobs = new();

    public Task AddAsync(ReviewJob job, CancellationToken ct = default)
    {
        this._jobs[job.Id] = job;
        return Task.CompletedTask;
    }

    public Task<TryAddReviewJobResult> TryAddIfNoActiveDuplicateAsync(ReviewJob job, CancellationToken ct = default)
    {
        var currentRevisionKey = ReviewRevisionKeys.TryGetStoredKey(job.ReviewRevisionReference);
        var activeJobs = this._jobs.Values
            .Where(candidate => string.Equals(candidate.OrganizationUrl, job.OrganizationUrl, StringComparison.Ordinal)
                                && string.Equals(candidate.ProjectId, job.ProjectId, StringComparison.Ordinal)
                                && RepositoryMatches(candidate, job.RepositoryId, job.ProjectId)
                                && candidate.PullRequestId == job.PullRequestId
                                && candidate.Status is JobStatus.Pending or JobStatus.Processing or JobStatus.BudgetHeld or JobStatus.BudgetExceeded)
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
            && job.Status is JobStatus.Pending or JobStatus.Processing or JobStatus.BudgetHeld or JobStatus.BudgetExceeded);
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
                job.ResultSummary,
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

    public Task<IReadOnlyList<ReviewJob>> GetStuckProcessingJobsAsync(TimeSpan threshold, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - threshold;
        return Task.FromResult<IReadOnlyList<ReviewJob>>(
            this._jobs.Values
                .Where(job => job.Status == JobStatus.Processing && job.ProcessingStartedAt < cutoff)
                .ToList()
                .AsReadOnly());
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
        if (this._jobs.TryGetValue(id, out var job) &&
            job.Status is not (JobStatus.Cancelled or JobStatus.Superseded or JobStatus.Stopped))
        {
            job.ErrorMessage = errorMessage;
            job.Status = JobStatus.Failed;
            job.CompletedAt = DateTimeOffset.UtcNow;
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        this._jobs.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task SetResultAsync(Guid id, ReviewResult result, CancellationToken ct = default)
    {
        if (this._jobs.TryGetValue(id, out var job) &&
            job.Status is not (JobStatus.Cancelled or JobStatus.Superseded or JobStatus.Stopped))
        {
            job.ApplyResult(result);
            job.Status = JobStatus.Completed;
            job.CompletedAt = DateTimeOffset.UtcNow;

            // Mirror the persistent store: a per-increment soft-capped run completes normally but records the
            // breach so it can be surfaced as soft-capped, distinct from a hard cut.
            if (result.BudgetSoftCapped
                && result.BudgetSoftCapThresholdUsd is { } softCapThreshold
                && result.BudgetSoftCapSpentUsd is { } softCapSpent)
            {
                job.SetBudgetBlock(BudgetScopeKind.Increment, BudgetCapKind.Soft, softCapThreshold, softCapSpent);
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
        if (this._jobs.TryGetValue(id, out var job) &&
            job.Status is not (JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Superseded or JobStatus.Stopped))
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = DateTimeOffset.UtcNow;
        }

        return Task.CompletedTask;
    }

    public Task SetSupersededAsync(Guid id, CancellationToken ct = default)
    {
        if (this._jobs.TryGetValue(id, out var job) &&
            job.Status is not (JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Superseded or JobStatus.Stopped))
        {
            job.Status = JobStatus.Superseded;
            job.CompletedAt = DateTimeOffset.UtcNow;
        }

        return Task.CompletedTask;
    }

    public Task SetStoppedAsync(Guid id, CancellationToken ct = default)
    {
        if (this._jobs.TryGetValue(id, out var job) &&
            job.Status is not (JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Superseded or JobStatus.Stopped))
        {
            job.Status = JobStatus.Stopped;
            job.CompletedAt = DateTimeOffset.UtcNow;
        }

        return Task.CompletedTask;
    }

    public Task SetBudgetExceededAsync(
        Guid id,
        BudgetScopeKind scope,
        BudgetCapKind capKind,
        decimal thresholdUsd,
        decimal spentUsd,
        CancellationToken ct = default)
    {
        if (this._jobs.TryGetValue(id, out var job) &&
            job.Status is not (JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Superseded or JobStatus.Stopped))
        {
            job.SetBudgetBlock(scope, capKind, thresholdUsd, spentUsd);
            job.Status = JobStatus.BudgetExceeded;
            job.CompletedAt = DateTimeOffset.UtcNow;
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
        if (this._jobs.TryGetValue(id, out var job) && job.Status == JobStatus.Pending)
        {
            job.SetBudgetBlock(scope, capKind, thresholdUsd, spentUsd);
            job.Status = JobStatus.BudgetHeld;
        }

        return Task.CompletedTask;
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
                    && job.Status is JobStatus.Pending or JobStatus.Processing or JobStatus.BudgetHeld or JobStatus.BudgetExceeded)
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
