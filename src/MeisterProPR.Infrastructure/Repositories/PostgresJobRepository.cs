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
    public bool TryTransition(Guid id, JobStatus from, JobStatus to)
    {
        // Use optimistic concurrency: load, check status, update, save
        // If another thread already transitioned, SaveChanges will see the conflict
        var job = dbContext.ReviewJobs.Find(id);
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
            dbContext.SaveChanges();
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            dbContext.Entry(job).Reload();
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
            .Where(j =>
                j.OrganizationUrl == organizationUrl &&
                j.ProjectId == projectId &&
                j.RepositoryId == repositoryId &&
                j.PullRequestId == pullRequestId &&
                j.IterationId == iterationId &&
                j.Status != JobStatus.Failed)
            .FirstOrDefault();
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
    public void Add(ReviewJob job)
    {
        dbContext.ReviewJobs.Add(job);
        dbContext.SaveChanges();
    }

    /// <inheritdoc />
    public void SetFailed(Guid id, string errorMessage)
    {
        var job = dbContext.ReviewJobs.Find(id);
        if (job is null)
        {
            return;
        }

        job.ErrorMessage = errorMessage;
        job.Status = JobStatus.Failed;
        job.CompletedAt = DateTimeOffset.UtcNow;
        dbContext.SaveChanges();
    }

    /// <inheritdoc />
    public void SetResult(Guid id, ReviewResult result)
    {
        var job = dbContext.ReviewJobs.Find(id);
        if (job is null)
        {
            return;
        }

        job.Result = result;
        job.Status = JobStatus.Completed;
        job.CompletedAt = DateTimeOffset.UtcNow;
        dbContext.SaveChanges();
    }
}
