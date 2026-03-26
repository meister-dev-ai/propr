using System.Collections.Concurrent;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>
///     Thread-safe in-memory implementation of <see cref="IJobRepository" /> backed by a
///     <see cref="ConcurrentDictionary{TKey,TValue}" />. Used in non-DB mode and for tests.
/// </summary>
public sealed class InMemoryJobRepository : IJobRepository
{
    private readonly ConcurrentDictionary<Guid, ReviewJob> _jobs = new();

    /// <inheritdoc />
    public Task<bool> TryTransitionAsync(Guid id, JobStatus from, JobStatus to, CancellationToken ct = default)
    {
        return Task.FromResult(this.TryTransition(id, from, to));
    }

    /// <inheritdoc />
    public IReadOnlyList<ReviewJob> GetAllForClient(Guid clientId)
    {
        return this._jobs.Values
            .Where(j => j.ClientId == clientId)
            .OrderByDescending(j => j.SubmittedAt)
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<ReviewJob> GetPendingJobs()
    {
        return this._jobs.Values
            .Where(j => j.Status == JobStatus.Pending)
            .OrderBy(j => j.SubmittedAt)
            .ToList();
    }

    /// <inheritdoc />
    public ReviewJob? FindActiveJob(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId)
    {
        return this._jobs.Values.FirstOrDefault(j =>
            j.OrganizationUrl == organizationUrl &&
            j.ProjectId == projectId &&
            j.RepositoryId == repositoryId &&
            j.PullRequestId == pullRequestId &&
            j.IterationId == iterationId &&
            (j.Status == JobStatus.Pending || j.Status == JobStatus.Processing));
    }

    /// <inheritdoc />
    public ReviewJob? GetById(Guid id)
    {
        return this._jobs.GetValueOrDefault(id);
    }

    /// <inheritdoc />
    public Task<(int total, IReadOnlyList<ReviewJob> items)> GetAllJobsAsync(
        int limit,
        int offset,
        JobStatus? status,
        Guid? clientId = null,
        CancellationToken ct = default)
    {
        var query = this._jobs.Values.AsEnumerable();
        if (status.HasValue)
        {
            query = query.Where(j => j.Status == status.Value);
        }

        if (clientId.HasValue)
        {
            query = query.Where(j => j.ClientId == clientId.Value);
        }

        var ordered = query.OrderByDescending(j => j.SubmittedAt).ToList();
        var total = ordered.Count;
        var items = (IReadOnlyList<ReviewJob>)ordered.Skip(offset).Take(limit).ToList();
        return Task.FromResult((total, items));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ReviewJob>> GetProcessingJobsAsync(CancellationToken ct = default)
    {
        var result = (IReadOnlyList<ReviewJob>)this._jobs.Values
            .Where(j => j.Status == JobStatus.Processing)
            .ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ReviewJob>> GetStuckProcessingJobsAsync(TimeSpan threshold, CancellationToken ct = default)
    {
        var staleBeforeUtc = DateTimeOffset.UtcNow - threshold;
        var result = (IReadOnlyList<ReviewJob>)this._jobs.Values
            .Where(j => j.Status == JobStatus.Processing && j.ProcessingStartedAt < staleBeforeUtc)
            .ToList();
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public Task AddAsync(ReviewJob job, CancellationToken ct = default)
    {
        this.Add(job);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateRetryCountAsync(Guid id, int retryCount, CancellationToken ct = default)
    {
        if (this._jobs.TryGetValue(id, out var job))
        {
            lock (job)
            {
                job.RetryCount = retryCount;
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetFailedAsync(Guid id, string errorMessage, CancellationToken ct = default)
    {
        if (this._jobs.TryGetValue(id, out var job))
        {
            lock (job)
            {
                job.ErrorMessage = errorMessage;
                job.Status = JobStatus.Failed;
                job.CompletedAt = DateTimeOffset.UtcNow;
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        this._jobs.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SetResultAsync(Guid id, ReviewResult result, CancellationToken ct = default)
    {
        if (this._jobs.TryGetValue(id, out var job))
        {
            lock (job)
            {
                job.Result = result;
                job.Status = JobStatus.Completed;
                job.CompletedAt = DateTimeOffset.UtcNow;
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ReviewJob?> GetByIdWithFileResultsAsync(Guid id, CancellationToken ct = default)
    {
        return Task.FromResult(this._jobs.GetValueOrDefault(id));
    }

    /// <inheritdoc />
    public Task AddFileResultAsync(ReviewFileResult result, CancellationToken ct = default)
    {
        if (this._jobs.TryGetValue(result.JobId, out var job))
        {
            lock (job)
            {
                job.FileReviewResults.Add(result);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task UpdateFileResultAsync(ReviewFileResult result, CancellationToken ct = default)
    {
        // In-memory update is implicit since the object is shared, 
        // but we ensure it's in the job's collection just in case.
        if (this._jobs.TryGetValue(result.JobId, out var job))
        {
            lock (job)
            {
                if (!job.FileReviewResults.Contains(result))
                {
                    job.FileReviewResults.Add(result);
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ReviewJob?> GetByIdWithProtocolsAsync(Guid id, CancellationToken ct = default)
    {
        return Task.FromResult(this._jobs.GetValueOrDefault(id));
    }

    /// <inheritdoc />
    [Obsolete("Use GetByIdWithProtocolsAsync instead.")]
    public Task<ReviewJob?> GetByIdWithProtocolAsync(Guid id, CancellationToken ct = default)
    {
        return this.GetByIdWithProtocolsAsync(id, ct);
    }

    /// <summary>Synchronous compare-and-swap on Status. Used internally and by tests.</summary>
    public bool TryTransition(Guid id, JobStatus from, JobStatus to)
    {
        if (!this._jobs.TryGetValue(id, out var job))
        {
            return false;
        }

        lock (job)
        {
            if (job.Status != from)
            {
                return false;
            }

            job.Status = to;
            return true;
        }
    }

    /// <summary>Synchronous add. Used by tests that construct jobs directly.</summary>
    public void Add(ReviewJob job)
    {
        this._jobs[job.Id] = job;
    }
}
