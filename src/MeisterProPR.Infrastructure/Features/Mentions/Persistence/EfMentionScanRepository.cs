// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>
///     EF Core implementation of <see cref="IMentionScanRepository" />.
///     Provides persistent watermark storage backed by PostgreSQL.
/// </summary>
public sealed class EfMentionScanRepository(MeisterProPRDbContext dbContext) : IMentionScanRepository
{
    /// <inheritdoc />
    public async Task<MentionProjectScan?> GetProjectScanAsync(Guid crawlConfigurationId, CancellationToken ct = default)
    {
        return await dbContext.MentionProjectScans
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.CrawlConfigurationId == crawlConfigurationId, ct);
    }

    /// <inheritdoc />
    public async Task UpsertProjectScanAsync(MentionProjectScan record, CancellationToken ct = default)
    {
        var updated = await dbContext.MentionProjectScans
            .Where(s => s.CrawlConfigurationId == record.CrawlConfigurationId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(p => p.LastScannedAt, record.LastScannedAt)
                    .SetProperty(p => p.UpdatedAt, DateTimeOffset.UtcNow),
                ct);

        if (updated == 0)
        {
            record.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.MentionProjectScans.AddAsync(record, ct);
            await dbContext.SaveChangesAsync(ct);
        }
    }

    /// <inheritdoc />
    public async Task<MentionPrScan?> GetPrScanAsync(
        Guid crawlConfigurationId,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct = default)
    {
        return await dbContext.MentionPrScans
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s =>
                    s.CrawlConfigurationId == crawlConfigurationId &&
                    s.RepositoryId == repositoryId &&
                    s.PullRequestId == pullRequestId,
                ct);
    }

    /// <inheritdoc />
    public async Task UpsertPrScanAsync(MentionPrScan record, CancellationToken ct = default)
    {
        var updated = await dbContext.MentionPrScans
            .Where(s =>
                s.CrawlConfigurationId == record.CrawlConfigurationId &&
                s.RepositoryId == record.RepositoryId &&
                s.PullRequestId == record.PullRequestId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(p => p.LastCommentSeenAt, record.LastCommentSeenAt)
                    .SetProperty(p => p.UpdatedAt, DateTimeOffset.UtcNow),
                ct);

        if (updated == 0)
        {
            record.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.MentionPrScans.AddAsync(record, ct);
            await dbContext.SaveChangesAsync(ct);
        }
    }
}
