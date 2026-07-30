// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.CodeInsights.Ports;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Classification;

/// <summary>
///     Drains the keyword backlog on resolution memories stored before keyword extraction existed.
/// </summary>
/// <remarks>
///     <para>
///         Reads and writes the memory rows directly rather than through the shared memory repository. The keyword
///         columns are additive metadata this slice owns; going through the repository would mean widening a
///         boundary the review path depends on for a catch-up that has nothing to do with reviewing.
///     </para>
///     <para>
///         Only the keyword column is written. A memory's embedding, summary, and resolution are never touched, so
///         a failed extraction leaves the memory exactly as useful as it was.
///     </para>
/// </remarks>
public sealed partial class CodeInsightMemoryKeywordSweeper(
    MeisterProPRDbContext dbContext,
    IMemoryKeywordExtractor extractor,
    ICodeInsightsCollectionGate gate,
    ILogger<CodeInsightMemoryKeywordSweeper> logger,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null) : ICodeInsightMemoryKeywordSweeper
{
    public async Task<int> SweepAsync(int maxMemories, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxMemories);

        try
        {
            return await this.WithDbAsync(db => this.SweepCoreAsync(db, maxMemories, ct), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogSweepFailed(logger, ex);
            return 0;
        }
    }

    private async Task<int> SweepCoreAsync(MeisterProPRDbContext db, int maxMemories, CancellationToken ct)
    {
        var candidates = await db.ThreadMemoryRecords
            .Where(record => record.Keywords.Count == 0)
            // Oldest first, so a backlog drains in the order it accumulated.
            .OrderBy(record => record.CreatedAt)
            .Take(maxMemories)
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            return 0;
        }

        var gateByClient = new Dictionary<Guid, bool>();
        var enriched = 0;

        foreach (var record in candidates)
        {
            if (!gateByClient.TryGetValue(record.ClientId, out var open))
            {
                open = await gate.IsCollectionEnabledAsync(record.ClientId, ct);
                gateByClient[record.ClientId] = open;
            }

            if (!open)
            {
                continue;
            }

            // One model call per memory, which is exactly why this sweep is opt-in and bounded.
            var keywords = await extractor.ExtractAsync(
                record.ClientId,
                record.ResolutionSummary,
                record.ChangeExcerpt,
                ct);

            if (keywords.Count == 0)
            {
                // Nothing extracted: a failed or empty result. Leaving the row untouched keeps it a candidate,
                // which is the right outcome for a transient failure and a bounded cost for a permanent one.
                continue;
            }

            record.Keywords = [.. keywords];
            enriched++;
        }

        if (enriched > 0)
        {
            await db.SaveChangesAsync(ct);
            LogSweepProgressed(logger, enriched);
        }

        return enriched;
    }

    private async Task<T> WithDbAsync<T>(Func<MeisterProPRDbContext, Task<T>> operation, CancellationToken ct)
    {
        if (contextFactory is null)
        {
            return await operation(dbContext);
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await operation(db);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Extracted search keywords for {MemoryCount} resolution memory record(s) that had none.")]
    private static partial void LogSweepProgressed(ILogger logger, int memoryCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The resolution-memory keyword backfill failed; the next sweep retries.")]
    private static partial void LogSweepFailed(ILogger logger, Exception ex);
}
