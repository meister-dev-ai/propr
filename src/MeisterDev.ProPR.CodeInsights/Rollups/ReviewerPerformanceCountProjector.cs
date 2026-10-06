// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Data;
using MeisterDev.ProPR.CodeInsights.Contracts;
using MeisterDev.ProPR.CodeInsights.Persistence;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MeisterDev.ProPR.CodeInsights.Rollups;

/// <summary>Replaces metadata-only joint count cells as evidence changes.</summary>
public sealed partial class ReviewerPerformanceCountProjector(
    MeisterProPRDbContext dbContext,
    ICodeInsightsCollectionGate gate,
    ILogger<ReviewerPerformanceCountProjector> logger,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null)
{
    private const int MaximumBackfillAggregates = 500;
    private const int MaximumWriteAttempts = 3;

    /// <summary>Current joint projection schema.</summary>
    public const int ProjectionVersion = 3;

    /// <summary>Refreshes the aggregate containing this job.</summary>
    public async Task ProjectJobAsync(Guid jobId, CancellationToken ct = default)
    {
        await this.WithDbAsync(
            async db =>
            {
                var ids = await db.CodeInsightFindings.Where(row => row.JobId == jobId).Select(row => row.CodeInsightPullRequestId).Distinct().ToListAsync(ct);
                foreach (var id in ids)
                {
                    await this.ProjectAsync(db, id, ct);
                }
            }, ct);
    }

    /// <summary>Refreshes the aggregate identified by the collection key.</summary>
    public async Task ProjectAsync(CodeInsightPullRequestKey key, CancellationToken ct = default)
    {
        await this.WithDbAsync(
            async db =>
            {
                var id = await db.CodeInsightPullRequests
                    .Where(row => row.ClientId == key.ClientId && row.RepositoryId == key.RepositoryId && row.PullRequestId == key.PullRequestId)
                    .Select(row => (Guid?)row.Id).FirstOrDefaultAsync(ct);
                if (id is not null)
                {
                    await this.ProjectAsync(db, id.Value, ct);
                }
            }, ct);
    }

    /// <summary>Refreshes all cells for one source aggregate.</summary>
    public Task ProjectPullRequestAsync(Guid aggregateId, CancellationToken ct = default) =>
        this.WithDbAsync(db => this.ProjectAsync(db, aggregateId, ct), ct);

    /// <summary>Repairs absent, old or stale projections in bounded batches.</summary>
    public async Task<int> BackfillAsync(int maximum, CancellationToken ct = default)
    {
        var count = 0;
        await this.WithDbAsync(
            async db =>
            {
                var clients = await db.CodeInsightPullRequests.Select(row => row.ClientId).Distinct().ToListAsync(ct);
                var enabled = new List<Guid>();
                foreach (var client in clients)
                {
                    if (await gate.IsCollectionEnabledAsync(client, ct))
                    {
                        enabled.Add(client);
                    }
                }

                var ids = await Metrics.ReviewerPerformanceProjectionQueries.Stale(db)
                    .Where(pr => enabled.Contains(pr.ClientId))
                    .OrderBy(pr => pr.PerformanceProjectedAt).Select(pr => pr.Id).Take(Math.Clamp(maximum, 1, MaximumBackfillAggregates)).ToListAsync(ct);
                foreach (var id in ids)
                {
                    await this.ProjectAsync(db, id, ct);
                    count++;
                }
            }, ct);
        return count;
    }

    private async Task ProjectAsync(MeisterProPRDbContext db, Guid aggregateId, CancellationToken ct)
    {
        var pr = await db.CodeInsightPullRequests.FirstOrDefaultAsync(row => row.Id == aggregateId, ct);
        if (pr is null || !await gate.IsCollectionEnabledAsync(pr.ClientId, ct))
        {
            return;
        }

        var projectionCutoff = DateTimeOffset.UtcNow;
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct) : null;
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({BitConverter.ToInt64(aggregateId.ToByteArray(), 0)})", ct);
        }

        if (db.Database.IsRelational())
        {
            await db.Entry(pr).ReloadAsync(ct);
        }

        var evidence = await ReviewerPerformanceProjectionEvidenceReader.LoadAsync(db, aggregateId, ct);
        var cells = ReviewerPerformanceProjectionBuilder.Build(pr, evidence, projectionCutoff);
        var stale = await db.ReviewerPerformanceDailyCounts.Where(row => row.CodeInsightPullRequestId == aggregateId).ToListAsync(ct);
        db.ReviewerPerformanceDailyCounts.RemoveRange(stale);
        db.ReviewerPerformanceDailyCounts.AddRange(cells);

        pr.PerformanceProjectionVersion = ProjectionVersion;
        pr.PerformanceProjectedAt = projectionCutoff;
        if (db.Database.IsRelational())
        {
            await db.CodeInsightPerformanceDirty.Where(row => row.CodeInsightPullRequestId == aggregateId).ExecuteDeleteAsync(ct);
        }
        else
        {
            db.CodeInsightPerformanceDirty.RemoveRange(
                await db.CodeInsightPerformanceDirty.Where(row => row.CodeInsightPullRequestId == aggregateId).ToListAsync(ct));
        }

        await db.SaveChangesAsync(ct);
        if (transaction is not null)
        {
            await transaction.CommitAsync(ct);
        }
    }

    private async Task WithDbAsync(Func<MeisterProPRDbContext, Task> action, CancellationToken ct)
    {
        try
        {
            for (var attempt = 0;; attempt++)
            {
                try
                {
                    await using var lease = await CodeInsightDbContextLease.CreateAsync(dbContext, contextFactory, ct);
                    await action(lease.Context);

                    break;
                }
                catch (PostgresException exception) when (attempt < MaximumWriteAttempts - 1 && exception.SqlState == "40001")
                {
                    if (contextFactory is null)
                    {
                        dbContext.ChangeTracker.Clear();
                    }
                }
                catch (DbUpdateException exception) when (attempt < MaximumWriteAttempts - 1 &&
                                                          exception.InnerException is PostgresException { SqlState: "40001" })
                {
                    if (contextFactory is null)
                    {
                        dbContext.ChangeTracker.Clear();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogProjectionFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reviewer performance count projection failed; retained source evidence is available for catch-up.")]
    private static partial void LogProjectionFailed(ILogger logger, Exception exception);
}
