// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Repositories;

/// <summary>
///     EF Core implementation of <see cref="IReviewPrScanRepository" />.
///     Provides persistent watermark storage backed by PostgreSQL.
/// </summary>
/// <remarks>
///     Each operation loads the record, changes only the columns its fact owns, and saves. Nothing is
///     deleted and re-added, so a write of one fact cannot roll back a concurrent write of another.
/// </remarks>
public sealed class EfReviewPrScanRepository(MeisterProPRDbContext dbContext) : IReviewPrScanRepository
{
    /// <inheritdoc />
    public async Task<ReviewPrScan?> GetAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
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
                    s.OrganizationUrl == organizationUrl &&
                    s.ProjectId == projectId &&
                    s.RepositoryId == repositoryId &&
                    s.PullRequestId == pullRequestId,
                ct);
    }

    /// <inheritdoc />
    public async Task SetReviewWatermarkAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        string revisionKey,
        CancellationToken ct = default)
    {
        var scan = await TrackScanAsync(dbContext, clientId, organizationUrl, projectId, repositoryId, pullRequestId, ct);

        if (scan is null)
        {
            await dbContext.ReviewPrScans.AddAsync(
                new ReviewPrScan(Guid.NewGuid(), clientId, organizationUrl, projectId, repositoryId, pullRequestId, revisionKey),
                ct);
        }
        else
        {
            scan.LastProcessedCommitId = revisionKey;
            scan.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task SetPendingReviewRevisionAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        string revisionKey,
        CancellationToken ct = default)
    {
        var scan = await TrackScanAsync(dbContext, clientId, organizationUrl, projectId, repositoryId, pullRequestId, ct);

        if (scan is null)
        {
            await dbContext.ReviewPrScans.AddAsync(
                ReviewPrScan.ForPendingReview(
                    Guid.NewGuid(),
                    clientId,
                    organizationUrl,
                    projectId,
                    repositoryId,
                    pullRequestId,
                    revisionKey,
                    DateTimeOffset.UtcNow),
                ct);
        }
        else if (!string.Equals(scan.PendingReviewRevisionKey, revisionKey, StringComparison.Ordinal))
        {
            // Only a change of revision restamps the clock. Re-declining the same revision every crawl tick
            // would otherwise keep resetting it, and the surface that reports how long a pull request has
            // been waiting would report how recently it was last looked at instead.
            scan.PendingReviewRevisionKey = revisionKey;
            scan.PendingReviewDetectedAt = DateTimeOffset.UtcNow;
            scan.UpdatedAt = DateTimeOffset.UtcNow;
        }
        else
        {
            return;
        }

        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task SetThreadPassWatermarkAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        string revisionKey,
        CancellationToken ct = default)
    {
        var scan = await TrackScanAsync(dbContext, clientId, organizationUrl, projectId, repositoryId, pullRequestId, ct);

        if (scan is null)
        {
            await dbContext.ReviewPrScans.AddAsync(
                ReviewPrScan.ForThreadPass(Guid.NewGuid(), clientId, organizationUrl, projectId, repositoryId, pullRequestId, revisionKey),
                ct);
        }
        else
        {
            scan.LastThreadPassRevisionKey = revisionKey;
            scan.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task SetLastSeenReplyCountsAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        IReadOnlyDictionary<string, int> replyCountByThreadId,
        CancellationToken ct = default)
    {
        if (replyCountByThreadId.Count == 0)
        {
            return;
        }

        var scan = await TrackScanAsync(dbContext, clientId, organizationUrl, projectId, repositoryId, pullRequestId, ct);
        if (scan is null)
        {
            return;
        }

        foreach (var (threadId, replyCount) in replyCountByThreadId)
        {
            MergeThread(scan, threadId).LastSeenReplyCount = replyCount;
        }

        scan.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task SetLastSeenStatusesAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        IReadOnlyDictionary<string, string?> statusByThreadId,
        CancellationToken ct = default)
    {
        if (statusByThreadId.Count == 0)
        {
            return;
        }

        var scan = await TrackScanAsync(dbContext, clientId, organizationUrl, projectId, repositoryId, pullRequestId, ct);
        if (scan is null)
        {
            return;
        }

        foreach (var (threadId, status) in statusByThreadId)
        {
            MergeThread(scan, threadId).LastSeenStatus = status;
        }

        scan.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task RetainOnlyThreadsAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        IReadOnlyCollection<string> threadIds,
        CancellationToken ct = default)
    {
        var scan = await TrackScanAsync(dbContext, clientId, organizationUrl, projectId, repositoryId, pullRequestId, ct);
        if (scan is null)
        {
            return;
        }

        var retained = threadIds.ToHashSet(StringComparer.Ordinal);
        var stale = scan.Threads.Where(thread => !retained.Contains(thread.ThreadId)).ToList();
        if (stale.Count == 0)
        {
            return;
        }

        dbContext.ReviewPrScanThreads.RemoveRange(stale);
        scan.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
    }

    private static async Task<ReviewPrScan?> TrackScanAsync(
        MeisterProPRDbContext dbContext,
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct)
    {
        return await dbContext.ReviewPrScans
            .Include(s => s.Threads)
            .FirstOrDefaultAsync(
                s =>
                    s.ClientId == clientId &&
                    s.OrganizationUrl == organizationUrl &&
                    s.ProjectId == projectId &&
                    s.RepositoryId == repositoryId &&
                    s.PullRequestId == pullRequestId,
                ct);
    }

    private static ReviewPrScanThread MergeThread(ReviewPrScan scan, string threadId)
    {
        var existing = scan.Threads.FirstOrDefault(thread =>
            string.Equals(thread.ThreadId, threadId, StringComparison.Ordinal));
        if (existing is not null)
        {
            return existing;
        }

        var added = new ReviewPrScanThread
        {
            ReviewPrScanId = scan.Id,
            ThreadId = threadId,
        };

        scan.Threads.Add(added);
        return added;
    }
}
