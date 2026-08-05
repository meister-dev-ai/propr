// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Mentions.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Repositories;

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
        string repositoryId,
        int pullRequestId,
        string threadId,
        long commentId,
        CancellationToken ct = default)
    {
        return await dbContext.MentionReplyJobs
            .AnyAsync(
                j =>
                    j.ClientId == clientId &&
                    j.RepositoryId == repositoryId &&
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
    public async Task SetCompletedAsync(Guid jobId, string? postedReplyCommentId, CancellationToken ct = default)
    {
        var job = await dbContext.MentionReplyJobs.FindAsync([jobId], ct);
        if (job is null)
        {
            return;
        }

        job.Status = MentionJobStatus.Completed;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.PostedReplyCommentId = NormalizeCommentId(postedReplyCommentId);
        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PostedMentionReply>> GetPostedRepliesAsync(
        DateTimeOffset completedAtOrAfter,
        int maxResults,
        CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxResults, 1);

        return await dbContext.MentionReplyJobs
            .AsNoTracking()
            .Where(j => j.Status == MentionJobStatus.Completed
                        && j.PostedReplyCommentId != null
                        && j.CompletedAt != null
                        && j.CompletedAt >= completedAtOrAfter)
            .OrderByDescending(j => j.CompletedAt)
            .Take(maxResults)
            .Select(j => new PostedMentionReply(
                j.Id,
                j.ClientId,
                j.RepositoryId,
                j.PullRequestId,
                j.ThreadId,
                j.PostedReplyCommentId!,
                j.CompletedAt!.Value))
            .ToListAsync(ct);
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

    // An adapter that reported no comment id, or reported whitespace, has told us nothing to attribute. Store
    // null for both so the recovery sweep's "knows its own comment id" filter means exactly that.
    private static string? NormalizeCommentId(string? postedReplyCommentId)
    {
        return string.IsNullOrWhiteSpace(postedReplyCommentId) ? null : postedReplyCommentId.Trim();
    }
}
