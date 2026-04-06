// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>
///     EF Core implementation of <see cref="IMentionReplyJobRepository" />.
///     Provides persistent storage for mention reply jobs backed by PostgreSQL.
/// </summary>
public sealed class EfMentionReplyJobRepository(MeisterProPRDbContext dbContext) : IMentionReplyJobRepository
{
    /// <inheritdoc />
    public async Task AddAsync(MentionReplyJob job, CancellationToken ct = default)
    {
        await dbContext.MentionReplyJobs.AddAsync(job, ct);
        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MentionReplyJob>> GetPendingAsync(CancellationToken ct = default)
    {
        return await dbContext.MentionReplyJobs
            .AsNoTracking()
            .Where(j => j.Status == MentionJobStatus.Pending)
            .OrderBy(j => j.CreatedAt)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsForCommentAsync(
        Guid clientId,
        int pullRequestId,
        int threadId,
        int commentId,
        CancellationToken ct = default)
    {
        return await dbContext.MentionReplyJobs
            .AnyAsync(
                j =>
                    j.ClientId == clientId &&
                    j.PullRequestId == pullRequestId &&
                    j.ThreadId == threadId &&
                    j.CommentId == commentId,
                ct);
    }

    /// <inheritdoc />
    public async Task<bool> TryTransitionAsync(
        Guid jobId,
        MentionJobStatus from,
        MentionJobStatus to,
        CancellationToken ct = default)
    {
        var job = await dbContext.MentionReplyJobs.FindAsync([jobId], ct);
        if (job is null || job.Status != from)
        {
            return false;
        }

        job.Status = to;
        if (to == MentionJobStatus.Processing)
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
            // Concurrency conflict occurred, another process likely updated the job. Reload the entity to get the latest state.
            await dbContext.Entry(job).ReloadAsync(ct);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task SetFailedAsync(Guid jobId, string errorMessage, CancellationToken ct = default)
    {
        var job = await dbContext.MentionReplyJobs.FindAsync([jobId], ct);
        if (job is null)
        {
            return;
        }

        job.Status = MentionJobStatus.Failed;
        job.ErrorMessage = errorMessage;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task SetCompletedAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await dbContext.MentionReplyJobs.FindAsync([jobId], ct);
        if (job is null)
        {
            return;
        }

        job.Status = MentionJobStatus.Completed;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task ResetStuckProcessingAsync(CancellationToken ct = default)
    {
        await dbContext.MentionReplyJobs
            .Where(j => j.Status == MentionJobStatus.Processing)
            .ExecuteUpdateAsync(
                s => s.SetProperty(j => j.Status, MentionJobStatus.Pending)
                    .SetProperty(j => j.ProcessingStartedAt, (DateTimeOffset?)null),
                ct);
    }
}
