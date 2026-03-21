using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>PostgreSQL-backed implementation of <see cref="IJobRepository" /> using EF Core.</summary>
public sealed class PostgresJobRepository(MeisterProPRDbContext dbContext) : IJobRepository
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
        return dbContext.ReviewJobs
            .Where(j => j.ClientId == clientId)
            .OrderByDescending(j => j.SubmittedAt)
            .ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<ReviewJob> GetPendingJobs()
    {
        return dbContext.ReviewJobs
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
        return dbContext.ReviewJobs
            .FirstOrDefault(j => j.OrganizationUrl == organizationUrl &&
                                 j.ProjectId == projectId &&
                                 j.RepositoryId == repositoryId &&
                                 j.PullRequestId == pullRequestId &&
                                 j.IterationId == iterationId &&
                                 j.Status != JobStatus.Failed);
    }

    /// <inheritdoc />
    public ReviewJob? GetById(Guid id)
    {
        return dbContext.ReviewJobs.Find(id);
    }

    /// <inheritdoc />
    public async Task<(int total, IReadOnlyList<ReviewJob> items)> GetAllJobsAsync(
        int limit,
        int offset,
        JobStatus? status,
        CancellationToken ct = default)
    {
        var query = dbContext.ReviewJobs.AsQueryable();
        if (status.HasValue)
        {
            query = query.Where(j => j.Status == status.Value);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(j => j.SubmittedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        return (total, items);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReviewJob>> GetProcessingJobsAsync(CancellationToken ct = default)
    {
        return await dbContext.ReviewJobs
            .Where(j => j.Status == JobStatus.Processing)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task AddAsync(ReviewJob job, CancellationToken ct = default)
    {
        dbContext.ReviewJobs.Add(job);
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
}
