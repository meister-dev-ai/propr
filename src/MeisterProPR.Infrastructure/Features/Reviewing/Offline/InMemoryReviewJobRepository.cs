// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using MeisterProPR.Application.Interfaces;
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
            && string.Equals(job.RepositoryId, repositoryId, StringComparison.Ordinal)
            && job.PullRequestId == pullRequestId
            && job.IterationId == iterationId
            && job.Status is JobStatus.Pending or JobStatus.Processing);
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
                && string.Equals(job.RepositoryId, repositoryId, StringComparison.Ordinal)
                && job.PullRequestId == pullRequestId
                && job.IterationId == iterationId
                && job.Status == JobStatus.Completed)
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

    public Task SetFailedAsync(Guid id, string errorMessage, CancellationToken ct = default)
    {
        if (this._jobs.TryGetValue(id, out var job))
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
        if (this._jobs.TryGetValue(id, out var job))
        {
            job.Result = result;
            job.Status = JobStatus.Completed;
            job.CompletedAt = DateTimeOffset.UtcNow;
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

    [Obsolete("Use GetByIdWithProtocolsAsync instead.")]
    public Task<ReviewJob?> GetByIdWithProtocolAsync(Guid id, CancellationToken ct = default)
    {
        return this.GetByIdWithProtocolsAsync(id, ct);
    }

    public Task SetCancelledAsync(Guid id, CancellationToken ct = default)
    {
        if (this._jobs.TryGetValue(id, out var job) && job.Status is not (JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled))
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = DateTimeOffset.UtcNow;
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
                    && job.Status is JobStatus.Pending or JobStatus.Processing)
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
}
