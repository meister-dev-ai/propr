using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Data;
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
                                 (j.Status == JobStatus.Pending || j.Status == JobStatus.Processing));
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
        Guid? clientId = null,
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

        var total = await query.CountAsync(ct);
        var items = await query
            .Include(j => j.Protocols)
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
        return await dbContext.ReviewJobs
            .Include(j => j.FileReviewResults)
            .FirstOrDefaultAsync(j => j.Id == id, ct);
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
        return await dbContext.ReviewJobs
            .Include(j => j.Protocols.OrderByDescending(p => p.AttemptNumber))
            .ThenInclude(p => p.Events.OrderBy(e => e.OccurredAt))
            .FirstOrDefaultAsync(j => j.Id == id, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReviewJob>> GetStuckProcessingJobsAsync(TimeSpan threshold, CancellationToken ct = default)
    {
        var staleBeforeUtc = DateTimeOffset.UtcNow - threshold;
        return await dbContext.ReviewJobs
            .Where(j => j.Status == JobStatus.Processing && j.ProcessingStartedAt < staleBeforeUtc)
            .ToListAsync(ct);
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
        string organizationUrl, string projectId, CancellationToken ct = default)
    {
        return await dbContext.ReviewJobs
            .Where(j => j.OrganizationUrl == organizationUrl &&
                        j.ProjectId == projectId &&
                        (j.Status == JobStatus.Pending || j.Status == JobStatus.Processing))
            .ToListAsync(ct);
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
        return await dbContext.ReviewJobs
            .Include(j => j.FileReviewResults)
            .Where(j => j.OrganizationUrl == organizationUrl &&
                        j.ProjectId == projectId &&
                        j.RepositoryId == repositoryId &&
                        j.PullRequestId == pullRequestId &&
                        j.IterationId == iterationId &&
                        j.Status == JobStatus.Completed)
            .OrderByDescending(j => j.CompletedAt)
            .FirstOrDefaultAsync(ct);
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
    public async Task UpdatePrContextAsync(Guid id, string? prTitle, string? prRepositoryName, string? prSourceBranch, string? prTargetBranch, CancellationToken ct = default)
    {
        var job = await dbContext.ReviewJobs.FindAsync([id], ct);
        if (job is null)
        {
            return;
        }

        job.SetPrContext(prTitle, prRepositoryName, prSourceBranch, prTargetBranch);
        await dbContext.SaveChangesAsync(ct);
    }
}
