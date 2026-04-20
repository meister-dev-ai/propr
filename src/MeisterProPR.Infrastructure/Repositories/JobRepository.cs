// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>Implementation of <see cref="IJobRepository" /> using EF Core.</summary>
public sealed class JobRepository(
    MeisterProPRDbContext dbContext,
    IDbContextFactory<MeisterProPRDbContext> contextFactory) : IJobRepository
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
            await dbContext.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            await dbContext.Entry(job).ReloadAsync(ct);
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
            .FirstOrDefault(j => j.OrganizationUrl == organizationUrl &&
                                 j.ProjectId == projectId &&
                                 j.RepositoryId == repositoryId &&
                                 j.PullRequestId == pullRequestId &&
                                 j.IterationId == iterationId &&
                                 (j.Status == JobStatus.Pending || j.Status == JobStatus.Processing));
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
            .Where(j => j.OrganizationUrl == organizationUrl &&
                        j.ProjectId == projectId &&
                        j.RepositoryId == repositoryId &&
                        j.PullRequestId == pullRequestId &&
                        j.IterationId == iterationId &&
                        j.Status == JobStatus.Completed)
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
    public async Task<IReadOnlyList<ReviewJob>> GetProcessingJobsAsync(CancellationToken ct = default)
    {
        var jobs = await dbContext.ReviewJobs
            .Where(j => j.Status == JobStatus.Processing)
            .ToListAsync(ct);

        await this.HydrateSourceScopesAsync(jobs, ct);
        return jobs;
    }

    /// <inheritdoc />
    public async Task AddAsync(ReviewJob job, CancellationToken ct = default)
    {
        dbContext.ReviewJobs.Add(job);

        if (job.ProCursorSourceScopeMode == ProCursorSourceScopeMode.SelectedSources)
        {
            foreach (var sourceId in job.ProCursorSourceIds)
            {
                dbContext.ReviewJobProCursorSourceScopes.Add(
                    new ReviewJobProCursorSourceScopeRecord
                    {
                        ReviewJobId = job.Id,
                        ProCursorSourceId = sourceId,
                        CreatedAt = DateTimeOffset.UtcNow,
                    });
            }
        }

        await dbContext.SaveChangesAsync(ct);
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
        await dbContext.SaveChangesAsync(ct);
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

        job.Result = result;
        job.Status = JobStatus.Completed;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
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
        var job = await dbContext.ReviewJobs
            .Include(j => j.Protocols.OrderByDescending(p => p.AttemptNumber))
            .ThenInclude(p => p.Events.OrderBy(e => e.OccurredAt))
            .FirstOrDefaultAsync(j => j.Id == id, ct);

        if (job is not null)
        {
            await this.HydrateSourceScopeAsync(job, ct);
        }

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
        if (job is null || job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled)
        {
            return;
        }

        job.Status = JobStatus.Cancelled;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
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
    public async Task UpdateAiConfigAsync(Guid id, Guid? connectionId, string? model, CancellationToken ct = default)
    {
        var job = await dbContext.ReviewJobs.FindAsync([id], ct);
        if (job is null)
        {
            return;
        }

        job.SetAiConfig(connectionId, model);
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
            .ToList()
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
}
