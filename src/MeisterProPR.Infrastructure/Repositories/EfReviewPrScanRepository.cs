// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>
///     EF Core implementation of <see cref="IReviewPrScanRepository" />.
///     Provides persistent watermark storage backed by PostgreSQL.
/// </summary>
public sealed class EfReviewPrScanRepository(MeisterProPRDbContext dbContext) : IReviewPrScanRepository
{
    /// <inheritdoc />
    public async Task<ReviewPrScan?> GetAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct = default)
    {
        return await dbContext.ReviewPrScans
            .Include(s => s.Threads)
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s =>
                    s.ClientId == clientId &&
                    s.RepositoryId == repositoryId &&
                    s.PullRequestId == pullRequestId,
                ct);
    }

    /// <inheritdoc />
    public async Task UpsertAsync(ReviewPrScan record, CancellationToken ct = default)
    {
        var existing = await dbContext.ReviewPrScans
            .Include(s => s.Threads)
            .FirstOrDefaultAsync(
                s =>
                    s.ClientId == record.ClientId &&
                    s.RepositoryId == record.RepositoryId &&
                    s.PullRequestId == record.PullRequestId,
                ct);

        if (existing is null)
        {
            record.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.ReviewPrScans.AddAsync(record, ct);
        }
        else
        {
            existing.LastProcessedCommitId = record.LastProcessedCommitId;
            existing.UpdatedAt = DateTimeOffset.UtcNow;

            // Replace child thread records.
            dbContext.ReviewPrScanThreads.RemoveRange(existing.Threads);

            foreach (var thread in record.Threads)
            {
                existing.Threads.Add(
                    new ReviewPrScanThread
                    {
                        ReviewPrScanId = existing.Id,
                        ThreadId = thread.ThreadId,
                        LastSeenReplyCount = thread.LastSeenReplyCount,
                        LastSeenStatus = thread.LastSeenStatus,
                    });
            }
        }

        await dbContext.SaveChangesAsync(ct);
    }
}
