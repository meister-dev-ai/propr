using MeisterDev.ProPR.Application.Features.Reviewing.Usage;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Usage;

/// <summary>Reconciles finalized reviews and exposes immutable keyset pages.</summary>
public sealed class CompletedReviewUsageExport(MeisterProPRDbContext db) : ICompletedReviewUsageExport
{
    public async Task<CompletedReviewUsagePage> GetPageAsync(Guid tenantId, Guid clientId, long? after, int limit, CancellationToken ct)
    {
        if (limit is < 1 or > 100 || after < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        if (!await db.Clients.AsNoTracking().AnyAsync(c => c.Id == clientId && c.TenantId == tenantId, ct))
        {
            return new CompletedReviewUsagePage([], null);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Writers for this client share a transaction lock. Each client has its own sequence watermark.
        var lockKey = $"propr:completed-review-usage:{clientId:N}";
        await PostgresAdvisoryLocks.AcquireTransactionAsync(db, lockKey, ct);

        var staleBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        // A host can stop between the terminal write and protocol cleanup. Reconcile only old terminal jobs.
        var staleIds = await db.ReviewJobs.AsNoTracking()
            .Where(j => j.ClientId == clientId && j.Status == JobStatus.Completed &&
                        j.ProcessingStartedAt != null &&
                        j.ExecutionDurationMilliseconds != null &&
                        j.UsageFinalizedAt == null && j.CompletedAt < staleBefore)
            .OrderBy(j => j.CompletedAt).ThenBy(j => j.Id)
            .Select(j => j.Id).Take(500).ToListAsync(ct);
        if (staleIds.Count > 0)
        {
            await db.ReviewJobProtocols
                .Where(p => p.JobId != null && staleIds.Contains(p.JobId.Value) && p.CompletedAt == null)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(p => p.CompletedAt, DateTimeOffset.UtcNow)
                        .SetProperty(p => p.Outcome, "Abandoned"), ct);
            var stillOpen = await db.ReviewJobProtocols.AsNoTracking()
                .Where(p => p.JobId != null && staleIds.Contains(p.JobId.Value) && p.CompletedAt == null)
                .Select(p => p.JobId!.Value).Distinct().ToListAsync(ct);
            var reconciledIds = staleIds.Except(stillOpen).ToArray();
            if (reconciledIds.Length > 0)
            {
                await db.ReviewJobs.Where(j => reconciledIds.Contains(j.Id) && j.Status == JobStatus.Completed)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.UsageFinalizedAt, DateTimeOffset.UtcNow), ct);
            }
        }

        var candidates = await db.ReviewJobs.AsNoTracking()
            .Where(j => j.ClientId == clientId && j.Status == JobStatus.Completed &&
                        j.ProcessingStartedAt != null && j.CompletedAt != null &&
                        j.ExecutionDurationMilliseconds != null && j.UsageFinalizedAt != null &&
                        !db.CompletedReviewUsageSnapshots.Any(x => x.JobId == j.Id))
            .OrderBy(j => j.CompletedAt).ThenBy(j => j.Id)
            .Take(500)
            .Select(j => new CompletedReviewUsageSnapshot
            {
                JobId = j.Id,
                ClientId = j.ClientId,
                CompletedAt = j.CompletedAt!.Value,
                ExecutionDurationMilliseconds = j.ExecutionDurationMilliseconds!.Value,
                AiConnectionId = j.AiConnectionId,
                EstimatedCostUsd = j.TotalEstimatedCostUsd,
                CostIsApproximate = j.CostIsApproximate,
                FinalizedAt = j.UsageFinalizedAt!.Value,
            }).ToListAsync(ct);
        db.CompletedReviewUsageSnapshots.AddRange(candidates);
        await db.SaveChangesAsync(ct);

        var rows = await db.CompletedReviewUsageSnapshots.AsNoTracking()
            .Where(x => x.ClientId == clientId && x.Sequence > (after ?? 0))
            .OrderBy(x => x.Sequence).Take(limit).ToListAsync(ct);
        await transaction.CommitAsync(ct);
        var items = rows.Select(x => new CompletedReviewUsageFact(
            x.Sequence, x.JobId, x.ClientId,
            x.CompletedAt, x.ExecutionDurationMilliseconds, x.AiConnectionId, x.EstimatedCostUsd,
            x.CostIsApproximate)).ToArray();
        return new CompletedReviewUsagePage(items, items.Length == 0 ? null : items[^1].Sequence);
    }
}
