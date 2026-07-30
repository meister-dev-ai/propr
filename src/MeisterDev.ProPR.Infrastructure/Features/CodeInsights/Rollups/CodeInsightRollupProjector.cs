// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.CodeInsights.Ports;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Rollups;

/// <summary>
///     Recomputes the daily count projection for one review job from the collected findings, their type tags,
///     and their recorded outcomes.
/// </summary>
/// <remarks>
///     Derive-and-replace, never increment. See <see cref="ICodeInsightRollupProjector" /> for why. The whole
///     job's cells are rewritten in one transaction, so a projection is never half-updated: either it reflects
///     the job as it now stands, or it reflects the job as it stood before.
/// </remarks>
public sealed partial class CodeInsightRollupProjector(
    MeisterProPRDbContext dbContext,
    ICodeInsightsCollectionGate gate,
    ILogger<CodeInsightRollupProjector> logger,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null) : ICodeInsightRollupProjector
{
    public async Task ProjectJobAsync(Guid jobId, CancellationToken ct = default)
    {
        try
        {
            await this.WithDbAsync(
                async db =>
                {
                    var findings = await db.CodeInsightFindings
                        .Where(finding => finding.JobId == jobId)
                        .Select(finding => new FindingRow(
                            finding.Id,
                            finding.CodeInsightPullRequestId,
                            finding.FilePath,
                            finding.ObservedAt,
                            finding.OriginSymbolName))
                        .ToListAsync(ct);

                    if (findings.Count == 0)
                    {
                        // Not necessarily nothing to do: the job may have had findings that were since purged,
                        // in which case its stale cells have to go rather than linger as phantom counts.
                        await ClearJobAsync(db, jobId, ct);
                        await db.SaveChangesAsync(ct);
                        return;
                    }

                    var aggregateIds = findings.Select(finding => finding.PullRequestAggregateId).Distinct().ToList();
                    var scopes = await db.CodeInsightPullRequests
                        .Where(pullRequest => aggregateIds.Contains(pullRequest.Id))
                        .Select(pullRequest => new
                        {
                            pullRequest.Id,
                            pullRequest.ClientId,
                            pullRequest.RepositoryId,
                            pullRequest.PullRequestId,
                        })
                        .ToDictionaryAsync(scope => scope.Id, ct);

                    // The gate is asked once, about the client that owns the work. A job belongs to one client
                    // by construction, so one answer covers the whole projection.
                    var clientId = scopes.Values.Select(scope => scope.ClientId).FirstOrDefault();
                    if (clientId == Guid.Empty || !await gate.IsCollectionEnabledAsync(clientId, ct))
                    {
                        return;
                    }

                    var findingIds = findings.Select(finding => finding.Id).ToList();

                    var coreTags = await db.CodeInsightFindingTags
                        .Where(tag => findingIds.Contains(tag.CodeInsightFindingId)
                                      && tag.IsCore
                                      && tag.CoreSlug != null)
                        .Select(tag => new { tag.CodeInsightFindingId, Slug = tag.CoreSlug! })
                        .ToListAsync(ct);

                    var dispositions = await db.CodeInsightFindingDispositions
                        .Where(disposition => findingIds.Contains(disposition.CodeInsightFindingId))
                        .Select(disposition => new
                        {
                            disposition.CodeInsightFindingId,
                            disposition.Disposition,
                        })
                        .ToListAsync(ct);

                    var tagsByFinding = coreTags
                        .GroupBy(tag => tag.CodeInsightFindingId)
                        .ToDictionary(group => group.Key, group => group.Select(tag => tag.Slug).ToList());
                    var dispositionByFinding = dispositions
                        .ToDictionary(entry => entry.CodeInsightFindingId, entry => entry.Disposition);

                    var cells = BuildCells(
                        findings, scopes.ToDictionary(
                            scope => scope.Key,
                            scope => new ScopeRow(scope.Value.ClientId, scope.Value.RepositoryId, scope.Value.PullRequestId)),
                        tagsByFinding,
                        dispositionByFinding);

                    // Replace rather than reconcile row by row. The job's cell set is small, and a wholesale
                    // rewrite is the only shape that cannot leave a stale cell behind when a finding's type or
                    // outcome changes between projections.
                    await ClearJobAsync(db, jobId, ct);

                    var now = DateTimeOffset.UtcNow;
                    foreach (var (key, count) in cells)
                    {
                        db.CodeInsightDailyCounts.Add(
                            new CodeInsightDailyCount
                            {
                                Id = Guid.CreateVersion7(),
                                ClientId = key.ClientId,
                                RepositoryId = key.RepositoryId,
                                PullRequestId = key.PullRequestId,
                                FilePath = key.FilePath,
                                JobId = jobId,
                                BucketDate = key.BucketDate,
                                Dimension = key.Dimension,
                                DimensionKey = key.DimensionKey,
                                Count = count,
                                UpdatedAt = now,
                            });
                    }

                    await db.SaveChangesAsync(ct);
                },
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A projection is a derived convenience. Losing one must never disturb the collection that produced
            // the facts, and the next touch of this job recomputes it anyway.
            LogProjectionFailed(logger, jobId, ex);
        }
    }

    public async Task<int> BackfillAsync(int maxJobs, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxJobs);

        List<Guid> candidates;

        try
        {
            candidates = await this.WithDbAsync(db => this.FindUnprojectedJobsAsync(db, maxJobs, ct), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogBackfillFailed(logger, ex);
            return 0;
        }

        var projected = 0;

        foreach (var jobId in candidates)
        {
            // Each job is projected on its own; ProjectJobAsync already swallows its own failures, so one bad
            // job costs its own cells and nothing else.
            await this.ProjectJobAsync(jobId, ct);
            projected++;
        }

        if (projected > 0)
        {
            LogBackfillProgressed(logger, projected);
        }

        return projected;
    }

    /// <summary>
    ///     Finds review jobs that have collected findings but no projected cells, restricted to clients whose
    ///     collection gate is open.
    /// </summary>
    /// <remarks>
    ///     The gate is applied while <em>selecting</em>, not after. An opted-out client's findings can never be
    ///     projected, so letting them into the batch would let them occupy it on every sweep and starve the
    ///     clients that can actually make progress.
    /// </remarks>
    private async Task<List<Guid>> FindUnprojectedJobsAsync(
        MeisterProPRDbContext db,
        int maxJobs,
        CancellationToken ct)
    {
        var clientIds = await db.CodeInsightPullRequests
            .Select(pullRequest => pullRequest.ClientId)
            .Distinct()
            .ToListAsync(ct);

        var open = new List<Guid>();
        foreach (var clientId in clientIds)
        {
            if (await gate.IsCollectionEnabledAsync(clientId, ct))
            {
                open.Add(clientId);
            }
        }

        if (open.Count == 0)
        {
            return [];
        }

        var aggregateIds = await db.CodeInsightPullRequests
            .Where(pullRequest => open.Contains(pullRequest.ClientId))
            .Select(pullRequest => pullRequest.Id)
            .ToListAsync(ct);

        return await db.CodeInsightFindings
            .Where(finding => aggregateIds.Contains(finding.CodeInsightPullRequestId))
            .Where(finding => !db.CodeInsightDailyCounts.Any(count => count.JobId == finding.JobId))
            // Oldest first, so a backlog drains in the order it accumulated.
            .OrderBy(finding => finding.CreatedAt)
            .Select(finding => finding.JobId)
            .Distinct()
            .Take(maxJobs)
            .ToListAsync(ct);
    }

    /// <summary>
    ///     Builds every cell the job contributes. A finding with several types contributes to several type
    ///     cells, deliberately: the counts answer "how many findings touch this type".
    /// </summary>
    private static Dictionary<CellKey, int> BuildCells(
        IReadOnlyList<FindingRow> findings,
        IReadOnlyDictionary<Guid, ScopeRow> scopes,
        IReadOnlyDictionary<Guid, List<string>> tagsByFinding,
        IReadOnlyDictionary<Guid, CodeInsightDisposition> dispositionByFinding)
    {
        var cells = new Dictionary<CellKey, int>();

        foreach (var finding in findings)
        {
            if (!scopes.TryGetValue(finding.PullRequestAggregateId, out var scope))
            {
                continue;
            }

            // The review's own observation time is the anchor, so a late-arriving outcome still lands in the
            // bucket the review belongs to.
            var bucket = DateOnly.FromDateTime(finding.ObservedAt.UtcDateTime);

            // The empty string stands for a pull-request-level finding: a real category, not missing data.
            var filePath = finding.FilePath ?? string.Empty;

            Add(cells, scope, filePath, bucket, CodeInsightCountDimension.FindingTotal, string.Empty);

            if (tagsByFinding.TryGetValue(finding.Id, out var slugs))
            {
                foreach (var slug in slugs)
                {
                    Add(cells, scope, filePath, bucket, CodeInsightCountDimension.CoreType, slug);
                }
            }

            if (dispositionByFinding.TryGetValue(finding.Id, out var disposition))
            {
                Add(
                    cells,
                    scope,
                    filePath,
                    bucket,
                    CodeInsightCountDimension.Disposition,
                    disposition.ToString());
            }

            // Only findings the file's syntax actually placed. A finding with no definition produces no cell, so a
            // symbol-grained total is smaller than a file-grained one, which a reader is told rather than
            // sold an "(unknown)" bucket that would rank as if it were a place in the code.
            if (!string.IsNullOrWhiteSpace(finding.SymbolName))
            {
                Add(
                    cells,
                    scope,
                    filePath,
                    bucket,
                    CodeInsightCountDimension.Symbol,
                    finding.SymbolName);
            }
        }

        return cells;
    }

    private static void Add(
        Dictionary<CellKey, int> cells,
        ScopeRow scope,
        string filePath,
        DateOnly bucket,
        CodeInsightCountDimension dimension,
        string dimensionKey)
    {
        var key = new CellKey(
            scope.ClientId,
            scope.RepositoryId,
            scope.PullRequestId,
            filePath,
            bucket,
            dimension,
            dimensionKey);

        cells[key] = cells.TryGetValue(key, out var existing) ? existing + 1 : 1;
    }

    private static async Task ClearJobAsync(MeisterProPRDbContext db, Guid jobId, CancellationToken ct)
    {
        var stale = await db.CodeInsightDailyCounts
            .Where(count => count.JobId == jobId)
            .ToListAsync(ct);
        db.CodeInsightDailyCounts.RemoveRange(stale);
    }

    private async Task WithDbAsync(Func<MeisterProPRDbContext, Task> operation, CancellationToken ct)
    {
        if (contextFactory is null)
        {
            await operation(dbContext);
            return;
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        await operation(db);
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
        Level = LogLevel.Warning,
        Message = "Projecting code-insight roll-ups for job {JobId} failed; the next touch recomputes them.")]
    private static partial void LogProjectionFailed(ILogger logger, Guid jobId, Exception ex);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Back-filled code-insight roll-ups for {JobCount} previously unprojected job(s).")]
    private static partial void LogBackfillProgressed(ILogger logger, int jobCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Selecting code-insight roll-up backfill candidates failed; the next sweep retries.")]
    private static partial void LogBackfillFailed(ILogger logger, Exception ex);

    private readonly record struct FindingRow(
        Guid Id,
        Guid PullRequestAggregateId,
        string? FilePath,
        DateTimeOffset ObservedAt,
        string? SymbolName);

    private readonly record struct ScopeRow(Guid ClientId, string RepositoryId, long PullRequestId);

    private readonly record struct CellKey(
        Guid ClientId,
        string RepositoryId,
        long PullRequestId,
        string FilePath,
        DateOnly BucketDate,
        CodeInsightCountDimension Dimension,
        string DimensionKey);
}
