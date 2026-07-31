// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using MeisterDev.ProPR.CodeInsights.Rollups;
using MeisterDev.ProPR.CodeInsights;

namespace MeisterDev.ProPR.CodeInsights.Rollups;

/// <summary>
///     Reads the projected daily counts, grouping them into whichever grain and bucket size the caller asks
///     for.
/// </summary>
/// <remarks>
///     Week and month buckets are derived rather than stored. The grouping is done in memory over the window's
///     day rows because the alternative (a provider-specific date-truncation expression) would not translate
///     under the in-memory provider the unit tests use, and the row count for a window is bounded by the window
///     itself rather than by history.
/// </remarks>
public sealed class CodeInsightRollupReader(
    MeisterProPRDbContext dbContext,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null) : ICodeInsightRollupReader
{
    public Task<IReadOnlyList<CodeInsightSeriesPoint>> GetSeriesAsync(
        CodeInsightRollupQuery query,
        CodeInsightCountDimension dimension,
        CodeInsightBucketSize bucketSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return this.WithDbAsync<IReadOnlyList<CodeInsightSeriesPoint>>(
            async db =>
            {
                if (query.ClientIds.Count == 0)
                {
                    return [];
                }

                var rows = await Filter(db.CodeInsightDailyCounts.AsNoTracking(), query)
                    .Where(count => count.Dimension == dimension)
                    .Select(count => new { count.BucketDate, count.DimensionKey, count.Count })
                    .ToListAsync(ct);

                return rows
                    .GroupBy(row => new { Bucket = BucketStart(row.BucketDate, bucketSize), row.DimensionKey })
                    .Select(group => new CodeInsightSeriesPoint(
                        group.Key.Bucket,
                        group.Key.DimensionKey,
                        group.Sum(row => row.Count)))
                    .OrderBy(point => point.BucketStart)
                    .ThenBy(point => point.DimensionKey, StringComparer.Ordinal)
                    .ToList();
            },
            ct);
    }

    public Task<IReadOnlyList<CodeInsightConcentrationRow>> GetConcentrationAsync(
        CodeInsightRollupQuery query,
        CodeInsightGrain grain,
        int topN,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topN);

        return this.WithDbAsync<IReadOnlyList<CodeInsightConcentrationRow>>(
            async db =>
            {
                if (query.ClientIds.Count == 0)
                {
                    return [];
                }

                // Concentration is about findings, not about types or outcomes: counting type rows would
                // over-weight a finding that happens to carry several.
                var rows = await Filter(db.CodeInsightDailyCounts.AsNoTracking(), query)
                    .Where(count => count.Dimension == CodeInsightCountDimension.FindingTotal)
                    .Select(count => new
                    {
                        count.ClientId,
                        count.RepositoryId,
                        count.PullRequestId,
                        count.FilePath,
                        count.JobId,
                        count.Count,
                    })
                    .ToListAsync(ct);

                // Names come from the aggregates rather than the projection: the counts key on the provider's
                // repository identifier, and denormalising a display name into every historical cell would leave
                // stale copies behind the first time a repository is renamed.
                var names = await CodeInsightRepositoryNames.LoadAsync(db, query.ClientIds, ct);

                return rows
                    .GroupBy(row => ScopeOf(grain, row.ClientId, row.RepositoryId, row.PullRequestId, row.FilePath, row.JobId))
                    .Select(group => new CodeInsightConcentrationRow(
                        group.Key.ClientId,
                        group.Key.RepositoryId,
                        group.Key.PullRequestId,
                        group.Key.FilePath,
                        group.Key.JobId,
                        group.Sum(row => row.Count),
                        NameOf(names, group.Key.ClientId, group.Key.RepositoryId)))
                    .OrderByDescending(row => row.Count)
                    // A stable tie-break, so an unchanged data set does not reshuffle a ranked list between reads.
                    .ThenBy(row => row.RepositoryId ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(row => row.FilePath ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(row => row.PullRequestId ?? 0)
                    .Take(topN)
                    .ToList();
            },
            ct);
    }

    /// <summary>
    ///     The recorded display name for a scope's repository, or <see langword="null" /> when none was recorded,
    ///     which leaves the caller showing the identifier rather than a blank.
    /// </summary>
    private static string? NameOf(
        IReadOnlyDictionary<(Guid ClientId, string RepositoryId), string> names,
        Guid clientId,
        string? repositoryId)
    {
        return repositoryId is not null && names.TryGetValue((clientId, repositoryId), out var name) ? name : null;
    }

    public Task<int> GetTotalAsync(CodeInsightRollupQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return this.WithDbAsync(
            async db =>
            {
                if (query.ClientIds.Count == 0)
                {
                    return 0;
                }

                return await Filter(db.CodeInsightDailyCounts.AsNoTracking(), query)
                    .Where(count => count.Dimension == CodeInsightCountDimension.FindingTotal)
                    .SumAsync(count => count.Count, ct);
            },
            ct);
    }

    public Task<CodeInsightRepositoryDirectory> GetRepositoryDirectoryAsync(
        CodeInsightRollupQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        return this.WithDbAsync(
            async db =>
            {
                if (query.ClientIds.Count == 0)
                {
                    return EmptyDirectory;
                }

                // Every repository the caller can see, whichever one the query happens to narrow to: this read is
                // what a reader chooses from, so narrowing it to the current choice would hide the alternatives.
                var scope = query with { RepositoryId = null, PullRequestId = null, FilePath = null };

                var rows = await Filter(db.CodeInsightDailyCounts.AsNoTracking(), scope)
                    .Where(count => count.Dimension == CodeInsightCountDimension.FindingTotal)
                    .Select(count => new DirectoryRow(
                        count.ClientId,
                        count.RepositoryId,
                        count.PullRequestId,
                        count.FilePath,
                        count.BucketDate,
                        count.Count))
                    .ToListAsync(ct);

                if (rows.Count == 0)
                {
                    return EmptyDirectory;
                }

                var names = await CodeInsightRepositoryNames.LoadAsync(db, query.ClientIds, ct);

                var repositories = rows
                    .GroupBy(row => (row.ClientId, row.RepositoryId))
                    .Select(group =>
                    {
                        var findings = group.Sum(row => row.Count);
                        var pullRequests = group.Select(row => row.PullRequestId).Distinct().Count();

                        return new CodeInsightRepositorySummary(
                            group.Key.ClientId,
                            group.Key.RepositoryId,
                            NameOf(names, group.Key.ClientId, group.Key.RepositoryId),
                            findings,
                            pullRequests,
                            // The empty path is a pull-request-level finding, which is not a file.
                            group.Where(row => row.FilePath.Length > 0).Select(row => row.FilePath).Distinct().Count(),
                            pullRequests == 0 ? null : (double)findings / pullRequests,
                            group.Max(row => row.BucketDate));
                    })
                    .OrderByDescending(repository => repository.Findings)
                    // A stable tie-break, so an unchanged data set does not reshuffle the list between reads.
                    .ThenBy(repository => repository.RepositoryName ?? repository.RepositoryId, StringComparer.Ordinal)
                    .ToList();

                var totalFindings = repositories.Sum(repository => repository.Findings);
                // Pull-request ids are per repository, so the pair is what makes a cross-repository count honest.
                var totalPullRequests = rows
                    .Select(row => (row.RepositoryId, row.PullRequestId))
                    .Distinct()
                    .Count();

                return new CodeInsightRepositoryDirectory(
                    totalFindings,
                    repositories.Count,
                    totalPullRequests,
                    totalPullRequests == 0 ? null : (double)totalFindings / totalPullRequests,
                    repositories);
            },
            ct);
    }

    public Task<CodeInsightHotspotReport> GetHotspotsAsync(
        CodeInsightRollupQuery query,
        long? filesFromPullRequestId,
        int topN,
        CodeInsightHotspotGrouping grouping = CodeInsightHotspotGrouping.File,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topN);

        return this.WithDbAsync(
            async db =>
            {
                if (query.ClientIds.Count == 0)
                {
                    return EmptyHotspots;
                }

                // A hotspot is a statement about history, so the caller's pull-request filter is dropped rather
                // than honoured: otherwise this would report one pull request's findings under a name promising
                // more. Which files to look at is a separate question, answered below.
                var history = query with { PullRequestId = null };

                List<string>? paths = null;
                if (filesFromPullRequestId is not null)
                {
                    paths = await Filter(db.CodeInsightDailyCounts.AsNoTracking(), history)
                        .Where(count => count.Dimension == CodeInsightCountDimension.FindingTotal
                                        && count.PullRequestId == filesFromPullRequestId.Value)
                        .Select(count => count.FilePath)
                        .Distinct()
                        .ToListAsync(ct);

                    if (paths.Count == 0)
                    {
                        return EmptyHotspots;
                    }
                }

                var rows = await LoadHotspotRowsAsync(db, history, paths, grouping, ct);

                if (grouping == CodeInsightHotspotGrouping.File)
                {
                    return Summarise(rows, topN);
                }

                // What the syntax could not place is a fact about the data, so it is measured rather than dropped:
                // the same scope at the file grain, minus what the symbol grain accounted for.
                var placed = rows.Sum(row => row.Count);
                var inScope = (await LoadHotspotRowsAsync(db, history, paths, CodeInsightHotspotGrouping.File, ct))
                    .Sum(row => row.Count);

                return Summarise(rows, topN) with { UnplacedFindings = Math.Max(inScope - placed, 0) };
            },
            ct);
    }

    private static async Task<List<HotspotRow>> LoadHotspotRowsAsync(
        MeisterProPRDbContext db,
        CodeInsightRollupQuery history,
        IReadOnlyCollection<string>? paths,
        CodeInsightHotspotGrouping grouping,
        CancellationToken ct)
    {
        var dimension = grouping == CodeInsightHotspotGrouping.Symbol
            ? CodeInsightCountDimension.Symbol
            : CodeInsightCountDimension.FindingTotal;

        var counts = Filter(db.CodeInsightDailyCounts.AsNoTracking(), history)
            .Where(count => count.Dimension == dimension);

        if (paths is not null)
        {
            var wanted = paths.ToList();
            counts = counts.Where(count => wanted.Contains(count.FilePath));
        }

        return await counts
            .Select(count => new HotspotRow(
                count.FilePath,
                grouping == CodeInsightHotspotGrouping.Symbol ? count.DimensionKey : null,
                count.PullRequestId,
                count.Count))
            .ToListAsync(ct);
    }

    /// <summary>
    ///     Turns the projected cells into per-file history plus the totals those rows sit inside.
    /// </summary>
    /// <remarks>
    ///     The totals are computed over every file, then the list is truncated: a caller asking for ten rows must
    ///     still be told what the whole scope amounts to, or a ranked list becomes a claim about the codebase.
    /// </remarks>
    private static CodeInsightHotspotReport Summarise(IReadOnlyList<HotspotRow> rows, int topN)
    {
        if (rows.Count == 0)
        {
            return EmptyHotspots;
        }

        var files = rows
            .GroupBy(row => (row.FilePath, row.SymbolName))
            .Select(group => new CodeInsightFileHotspot(
                group.Key.FilePath,
                group.Sum(row => row.Count),
                group.Select(row => row.PullRequestId).Distinct().Count(),
                Average(group.Sum(row => row.Count), group.Select(row => row.PullRequestId).Distinct().Count()),
                group.Key.SymbolName))
            .OrderByDescending(file => file.Findings)
            // A stable tie-break, so an unchanged data set does not reshuffle a ranked list between reads.
            .ThenBy(file => file.FilePath, StringComparer.Ordinal)
            .ThenBy(file => file.SymbolName ?? string.Empty, StringComparer.Ordinal)
            .ToList();

        var totalFindings = files.Sum(file => file.Findings);
        var pullRequests = rows.Select(row => row.PullRequestId).Distinct().Count();

        return new CodeInsightHotspotReport(
            totalFindings,
            pullRequests,
            Average(totalFindings, pullRequests),
            files.Count,
            files.Take(topN).ToList());
    }

    private static double? Average(int findings, int pullRequests)
    {
        return pullRequests == 0 ? null : (double)findings / pullRequests;
    }

    private static readonly CodeInsightHotspotReport EmptyHotspots = new(0, 0, null, 0, [], 0);

    private static readonly CodeInsightRepositoryDirectory EmptyDirectory = new(0, 0, 0, null, []);

    /// <summary>
    ///     Applies the window, the authorised client set, and any optional scope narrowing. The client filter is
    ///     applied unconditionally: there is no code path that reads across clients the caller did not supply.
    /// </summary>
    private static IQueryable<CodeInsightDailyCount> Filter(
        IQueryable<CodeInsightDailyCount> source,
        CodeInsightRollupQuery query)
    {
        var clientIds = query.ClientIds.ToList();
        var filtered = source
            .Where(count => clientIds.Contains(count.ClientId))
            .Where(count => count.BucketDate >= query.From && count.BucketDate <= query.To);

        if (query.RepositoryId is not null)
        {
            filtered = filtered.Where(count => count.RepositoryId == query.RepositoryId);
        }

        if (query.PullRequestId is not null)
        {
            filtered = filtered.Where(count => count.PullRequestId == query.PullRequestId.Value);
        }

        if (query.FilePath is not null)
        {
            filtered = filtered.Where(count => count.FilePath == query.FilePath);
        }

        return filtered;
    }

    /// <summary>Reduces a day to the start of the bucket it belongs to.</summary>
    internal static DateOnly BucketStart(DateOnly day, CodeInsightBucketSize bucketSize)
    {
        return bucketSize switch
        {
            // Monday-anchored, so a week is the same week regardless of the reader's locale.
            CodeInsightBucketSize.Week => day.AddDays(-(((int)day.DayOfWeek + 6) % 7)),
            CodeInsightBucketSize.Month => new DateOnly(day.Year, day.Month, 1),
            _ => day,
        };
    }

    /// <summary>Blanks the scope parts a grain does not group by, so they collapse into one row.</summary>
    private static ScopeKey ScopeOf(
        CodeInsightGrain grain,
        Guid clientId,
        string repositoryId,
        long pullRequestId,
        string filePath,
        Guid jobId)
    {
        return grain switch
        {
            CodeInsightGrain.Client => new ScopeKey(clientId, null, null, null, null),
            CodeInsightGrain.Repository => new ScopeKey(clientId, repositoryId, null, null, null),
            CodeInsightGrain.PullRequest => new ScopeKey(clientId, repositoryId, pullRequestId, null, null),
            CodeInsightGrain.File => new ScopeKey(clientId, repositoryId, null, filePath, null),
            _ => new ScopeKey(clientId, repositoryId, pullRequestId, null, jobId),
        };
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

    private readonly record struct ScopeKey(
        Guid ClientId,
        string? RepositoryId,
        long? PullRequestId,
        string? FilePath,
        Guid? JobId);

    private readonly record struct HotspotRow(string FilePath, string? SymbolName, long PullRequestId, int Count);

    private readonly record struct DirectoryRow(
        Guid ClientId,
        string RepositoryId,
        long PullRequestId,
        string FilePath,
        DateOnly BucketDate,
        int Count);
}
