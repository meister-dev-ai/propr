// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using MeisterDev.ProPR.CodeInsights.Metrics;
using MeisterDev.ProPR.CodeInsights.Rollups;
using MeisterDev.ProPR.CodeInsights.Taxonomy;
using MeisterDev.ProPR.CodeInsights;

namespace MeisterDev.ProPR.CodeInsights.Metrics;

/// <summary>
///     Reads the two metric lenses: correctness from the sealed per-pull-request snapshots, acceptance from the
///     live count projection.
/// </summary>
/// <remarks>
///     <para>
///         Every aggregation sums the stored counts and divides once. Averaging the per-pull-request ratios
///         would weight a pull request with one finding the same as one with a hundred, which is not a
///         defensible answer to "how good is the reviewer on this repository".
///     </para>
///     <para>
///         Acceptance comes from the projected outcome counts rather than from a second pass over the
///         disposition table. The projection is recomputed the moment an outcome is recorded, so it is as live
///         as the dispositions themselves, and it already carries every scope part as a real column: which
///         means one filtering path, tested once, instead of two that can disagree about what a client is
///         allowed to see.
///     </para>
///     <para>
///         The two lenses therefore date a window differently, and deliberately. Acceptance is a cohort: the
///         window selects findings by when they were <em>reviewed</em>, so a period's acceptance rate keeps
///         maturing as its findings resolve. Correctness is a snapshot: the window selects pull requests by when
///         they were <em>sealed</em>, and a sealed period never moves again. Reporting acceptance by resolution
///         date instead would need a second time axis on the projection, and a maturing cohort is the more
///         useful reading as long as the sample size travels with it, which it does.
///     </para>
/// </remarks>
public sealed class CodeInsightMetricReader(
    MeisterProPRDbContext dbContext,
    ICodeInsightRollupReader rollupReader,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null) : ICodeInsightMetricReader
{
    public async Task<CodeInsightMetricResult> GetCorrectnessAsync(
        CodeInsightRollupQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var seals = await this.LoadSealsAsync(query, ct);
        return Summarise(seals);
    }

    public async Task<IReadOnlyList<CodeInsightScopedMetricResult>> GetCorrectnessByGrainAsync(
        CodeInsightRollupQuery query,
        CodeInsightGrain grain,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var seals = await this.LoadSealsAsync(query, ct);
        var names = await this.WithDbAsync(db => CodeInsightRepositoryNames.LoadAsync(db, query.ClientIds, ct), ct);

        return seals
            .GroupBy(seal => ScopeOf(grain, seal))
            .Select(group => new CodeInsightScopedMetricResult(
                group.Key.ClientId,
                group.Key.RepositoryId,
                group.Key.PullRequestId,
                Summarise(group.ToList()),
                group.Key.RepositoryId is not null && names.TryGetValue((group.Key.ClientId, group.Key.RepositoryId), out var name)
                    ? name
                    : null))
            .OrderBy(row => row.RepositoryId ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(row => row.PullRequestId ?? 0)
            .ToList();
    }

    public async Task<IReadOnlyList<CodeInsightMetricSeriesPoint>> GetCorrectnessSeriesAsync(
        CodeInsightRollupQuery query,
        CodeInsightBucketSize bucketSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var seals = await this.LoadSealsAsync(query, ct);

        return seals
            .GroupBy(seal => CodeInsightRollupReader.BucketStart(seal.SealedOn, bucketSize))
            .Select(group => new CodeInsightMetricSeriesPoint(group.Key, Summarise(group.ToList())))
            .OrderBy(point => point.BucketStart)
            .ToList();
    }

    public async Task<CodeInsightMetricResult> GetAcceptanceAsync(
        CodeInsightRollupQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var series = await this.LoadOutcomeSeriesAsync(query, CodeInsightBucketSize.Day, ct);

        return Accept(series.SelectMany(bucket => bucket.Counts));
    }

    public async Task<IReadOnlyList<CodeInsightMetricSeriesPoint>> GetAcceptanceSeriesAsync(
        CodeInsightRollupQuery query,
        CodeInsightBucketSize bucketSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var series = await this.LoadOutcomeSeriesAsync(query, bucketSize, ct);

        return series
            .Select(bucket => new CodeInsightMetricSeriesPoint(bucket.BucketStart, Accept(bucket.Counts)))
            .OrderBy(point => point.BucketStart)
            .ToList();
    }

    public async Task<IReadOnlyList<CodeInsightModelMetricResult>> GetByModelAsync(
        CodeInsightRollupQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.ClientIds.Count == 0)
        {
            return [];
        }

        var rows = await this.LoadModelOutcomesAsync(query, ct);

        return rows
            .GroupBy(row => new ModelKey(row.ModelId, row.LogicalModelName))
            .Select(group =>
            {
                var inputs = CodeInsightMetricInputs.Sum(group.Select(row => Single(row.Disposition)));

                return new CodeInsightModelMetricResult(
                    group.Key.ModelId,
                    group.Key.LogicalModelName,

                    // The sample is the resolved findings this model produced: it is what both attributable ratios
                    // are a proportion of, and what a view needs in order to refuse to draw a thin one.
                    new CodeInsightMetricResult(CodeInsightMetricCalculator.ComputeAttributable(inputs), inputs.Resolved));
            })
            // Worst first, on the only correctness ratio a model can be held to. A row with no computable precision
            // sorts last rather than as a zero it never earned.
            .OrderBy(row => row.Result.Metrics.Precision ?? double.MaxValue)
            .ThenByDescending(row => row.Result.SampleSize)
            .ToList();
    }

    public Task<CodeInsightRejectionReasonBreakdown> GetRejectionReasonsAsync(
        CodeInsightRollupQuery query,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.ClientIds.Count == 0)
        {
            return Task.FromResult(CodeInsightRejectionReasonBreakdown.Empty);
        }

        return this.WithDbAsync(
            async db =>
            {
                var rows = await this.LoadRejectionsAsync(db, query, ct);
                if (rows.Count == 0)
                {
                    return CodeInsightRejectionReasonBreakdown.Empty;
                }

                var classes = await LoadConcernClassesAsync(db, rows.Select(row => row.FindingId).ToList(), ct);

                return new CodeInsightRejectionReasonBreakdown(
                    CountByReason(rows.Select(row => row.Reason)),
                    rows.Count(row => row.Reason is null),
                    rows.Count,
                    rows
                        .GroupBy(row => classes.TryGetValue(row.FindingId, out var concern) ? concern : null)
                        .Select(group => new CodeInsightConcernClassRejections(
                            group.Key,
                            CountByReason(group.Select(row => row.Reason)),
                            group.Count(row => row.Reason is null),
                            group.Count()))
                        // Functional first, then evolvability, then the findings that carry no core type, so the
                        // order does not depend on which rows the database happened to return first.
                        .OrderBy(row => row.ConcernClass is null ? 2 : (int)row.ConcernClass.Value)
                        .ToList());
            },
            ct);
    }

    /// <summary>
    ///     How many rejections fall under each reason. Rejections whose reason could not be determined are left out
    ///     rather than bucketed, because the count of those is reported on its own beside this.
    /// </summary>
    private static Dictionary<CodeInsightRejectionReason, int> CountByReason(IEnumerable<CodeInsightRejectionReason?> reasons)
    {
        return reasons
            .Where(reason => reason is not null)
            .GroupBy(reason => reason!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
    }

    /// <summary>
    ///     The concern class of each named finding, absent where the finding carries no core type. Derived from
    ///     the characteristic the taxonomy already assigns each type, so no model call and no new column.
    /// </summary>
    private static async Task<Dictionary<Guid, CodeInsightConcernClass?>> LoadConcernClassesAsync(
        MeisterProPRDbContext db,
        IReadOnlyList<Guid> findingIds,
        CancellationToken ct)
    {
        var tags = await db.CodeInsightFindingTags
            .AsNoTracking()
            .Where(tag => findingIds.Contains(tag.CodeInsightFindingId) && tag.IsCore && tag.CoreSlug != null)
            .Select(tag => new { tag.CodeInsightFindingId, tag.CoreSlug })
            .ToListAsync(ct);

        return tags
            .GroupBy(tag => tag.CodeInsightFindingId)
            .ToDictionary(
                group => group.Key,
                group => CodeInsightCoreTaxonomy.ConcernClassOf(group.Select(tag => tag.CoreSlug)));
    }

    /// <summary>
    ///     Loads the reason of every rejected finding in scope, nulls included, so the caller can report the
    ///     unclassified remainder rather than inferring it from a total that does not match.
    /// </summary>
    private async Task<List<RejectionRow>> LoadRejectionsAsync(
        MeisterProPRDbContext db,
        CodeInsightRollupQuery query,
        CancellationToken ct)
    {
        var clientIds = query.ClientIds.ToList();
        var (from, toExclusive) = FindingWindow(query);

        var scopes = db.CodeInsightPullRequests
            .AsNoTracking()
            // Unconditional, like every other code-insight read.
            .Where(pullRequest => clientIds.Contains(pullRequest.ClientId));

        if (query.RepositoryId is not null)
        {
            scopes = scopes.Where(pullRequest => pullRequest.RepositoryId == query.RepositoryId);
        }

        if (query.PullRequestId is not null)
        {
            scopes = scopes.Where(pullRequest => pullRequest.PullRequestId == query.PullRequestId.Value);
        }

        var scopeIds = scopes.Select(pullRequest => pullRequest.Id);

        var findings = db.CodeInsightFindings
            .AsNoTracking()
            .Where(finding => scopeIds.Contains(finding.CodeInsightPullRequestId))
            .Where(finding => finding.ObservedAt >= from && finding.ObservedAt < toExclusive);

        if (query.FilePath is not null)
        {
            findings = findings.Where(finding => finding.FilePath == query.FilePath);
        }

        return await findings
            .Join(
                db.CodeInsightFindingDispositions.AsNoTracking(),
                finding => finding.Id,
                disposition => disposition.CodeInsightFindingId,
                (finding, disposition) => disposition)
            .Where(disposition => disposition.Disposition == CodeInsightDisposition.Dismissed
                                  || disposition.Disposition == CodeInsightDisposition.FalsePositive)
            .Select(disposition => new RejectionRow(
                disposition.CodeInsightFindingId,
                disposition.RejectionReason))
            .ToListAsync(ct);
    }

    /// <summary>One rejected finding: its identity, so its concern class can be resolved, and its reason.</summary>
    private sealed record RejectionRow(Guid FindingId, CodeInsightRejectionReason? Reason);

    /// <summary>
    ///     Loads one row per resolved finding in scope: which model produced it and what became of it.
    /// </summary>
    /// <remarks>
    ///     Only findings that reached an outcome are loaded. An unresolved finding contributes to neither ratio, so
    ///     carrying it would inflate nothing but the row count, and the join is what bounds this read.
    /// </remarks>
    private Task<List<ModelOutcomeRow>> LoadModelOutcomesAsync(
        CodeInsightRollupQuery query,
        CancellationToken ct)
    {
        return this.WithDbAsync(
            async db =>
            {
                var clientIds = query.ClientIds.ToList();
                var (from, toExclusive) = FindingWindow(query);

                var scopes = db.CodeInsightPullRequests
                    .AsNoTracking()
                    // Unconditional, like every other code-insight read.
                    .Where(pullRequest => clientIds.Contains(pullRequest.ClientId));

                if (query.RepositoryId is not null)
                {
                    scopes = scopes.Where(pullRequest => pullRequest.RepositoryId == query.RepositoryId);
                }

                if (query.PullRequestId is not null)
                {
                    scopes = scopes.Where(pullRequest => pullRequest.PullRequestId == query.PullRequestId.Value);
                }

                var scopeIds = scopes.Select(pullRequest => pullRequest.Id);

                var findings = db.CodeInsightFindings
                    .AsNoTracking()
                    .Where(finding => scopeIds.Contains(finding.CodeInsightPullRequestId))
                    .Where(finding => finding.ObservedAt >= from && finding.ObservedAt < toExclusive);

                if (query.FilePath is not null)
                {
                    findings = findings.Where(finding => finding.FilePath == query.FilePath);
                }

                return await findings
                    .Join(
                        db.CodeInsightFindingDispositions.AsNoTracking(),
                        finding => finding.Id,
                        disposition => disposition.CodeInsightFindingId,
                        (finding, disposition) => new ModelOutcomeRow(
                            finding.OriginModelId,
                            finding.OriginLogicalModelName,
                            disposition.Disposition))
                    .ToListAsync(ct);
            },
            ct);
    }

    /// <summary>Turns one finding's outcome into the counted inputs, with no miss to charge to anybody.</summary>
    private static CodeInsightMetricInputs Single(CodeInsightDisposition disposition)
    {
        return new CodeInsightMetricInputs(
            disposition == CodeInsightDisposition.Addressed ? 1 : 0,
            disposition == CodeInsightDisposition.Acknowledged ? 1 : 0,
            disposition == CodeInsightDisposition.Dismissed ? 1 : 0,
            disposition == CodeInsightDisposition.FalsePositive ? 1 : 0,
            Misses: 0,
            disposition == CodeInsightDisposition.Discussed ? 1 : 0);
    }

    /// <summary>
    ///     Turns the inclusive date window into a half-open instant range, so a review that ran late on the last
    ///     day of the window is inside it. The finding's own observation time is the anchor, which is the same axis
    ///     the acceptance lens uses.
    /// </summary>
    private static (DateTimeOffset From, DateTimeOffset ToExclusive) FindingWindow(CodeInsightRollupQuery query)
    {
        return (
            new DateTimeOffset(query.From.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            new DateTimeOffset(query.To.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
    }

    private async Task<List<OutcomeBucket>> LoadOutcomeSeriesAsync(
        CodeInsightRollupQuery query,
        CodeInsightBucketSize bucketSize,
        CancellationToken ct)
    {
        var series = await rollupReader.GetSeriesAsync(
            query,
            CodeInsightCountDimension.Disposition,
            bucketSize,
            ct);

        return series
            .GroupBy(point => point.BucketStart)
            .Select(group => new OutcomeBucket(
                group.Key,
                group.Select(point => new KeyValuePair<string, int>(point.DimensionKey, point.Count)).ToList()))
            .OrderBy(bucket => bucket.BucketStart)
            .ToList();
    }

    /// <summary>Computes the acceptance lens over a set of per-outcome counts.</summary>
    private static CodeInsightMetricResult Accept(IEnumerable<KeyValuePair<string, int>> counts)
    {
        var byOutcome = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (outcome, count) in counts)
        {
            byOutcome[outcome] = byOutcome.TryGetValue(outcome, out var existing) ? existing + count : count;
        }

        // Misses are deliberately zero here: this lens asks what humans did with the findings the reviewer
        // raised, and a miss is by definition not one of them.
        var inputs = new CodeInsightMetricInputs(
            Count(byOutcome, CodeInsightDisposition.Addressed),
            Count(byOutcome, CodeInsightDisposition.Acknowledged),
            Count(byOutcome, CodeInsightDisposition.Dismissed),
            Count(byOutcome, CodeInsightDisposition.FalsePositive),
            Misses: 0,
            Count(byOutcome, CodeInsightDisposition.Discussed));

        // The sample is the resolved findings, not the pull requests: this is what an acceptance rate is a
        // proportion of, and it is what a view needs to decide whether the number is worth drawing.
        return new CodeInsightMetricResult(CodeInsightMetricCalculator.Compute(inputs), inputs.Resolved);
    }

    private static int Count(IReadOnlyDictionary<string, int> byOutcome, CodeInsightDisposition disposition)
    {
        return byOutcome.TryGetValue(disposition.ToString(), out var count) ? count : 0;
    }

    private static CodeInsightMetricResult Summarise(IReadOnlyList<SealRow> seals)
    {
        var inputs = CodeInsightMetricInputs.Sum(
            seals.Select(seal => new CodeInsightMetricInputs(
                seal.Addressed,
                seal.Acknowledged,
                seal.Dismissed,
                seal.FalsePositive,
                seal.Misses,
                seal.Discussed)));

        // The sample is the number of sealed pull requests. A period whose F1 rests on two pull requests must
        // be distinguishable from one that rests on two hundred, whatever the ratio happens to be.
        return new CodeInsightMetricResult(CodeInsightMetricCalculator.Compute(inputs), seals.Count);
    }

    private static ScopeKey ScopeOf(CodeInsightGrain grain, SealRow seal)
    {
        return grain switch
        {
            CodeInsightGrain.Client => new ScopeKey(seal.ClientId, null, null),
            CodeInsightGrain.Repository => new ScopeKey(seal.ClientId, seal.RepositoryId, null),

            // A seal is per-pull-request, so there is no finer grain to group at: the file and job grains the
            // count projection supports have no counterpart here, and collapsing them onto the pull request is
            // the honest answer rather than an empty result.
            _ => new ScopeKey(seal.ClientId, seal.RepositoryId, seal.PullRequestId),
        };
    }

    private async Task<List<SealRow>> LoadSealsAsync(CodeInsightRollupQuery query, CancellationToken ct)
    {
        if (query.ClientIds.Count == 0)
        {
            return [];
        }

        return await this.WithDbAsync(
            async db =>
            {
                var clientIds = query.ClientIds.ToList();
                var seals = db.CodeInsightPullRequestMetrics
                    .AsNoTracking()
                    // Unconditional, like every other code-insight read: there is no path that aggregates over
                    // clients the caller did not supply.
                    .Where(metric => clientIds.Contains(metric.ClientId))
                    .Where(metric => metric.SealedOn >= query.From && metric.SealedOn <= query.To);

                if (query.RepositoryId is not null)
                {
                    seals = seals.Where(metric => metric.RepositoryId == query.RepositoryId);
                }

                if (query.PullRequestId is not null)
                {
                    seals = seals.Where(metric => metric.PullRequestId == query.PullRequestId.Value);
                }

                // A file filter has no meaning for a per-pull-request seal and is deliberately ignored rather
                // than silently returning nothing: the correctness lens does not exist at file granularity.
                var rows = await seals
                    .Select(metric => new
                    {
                        metric.ClientId,
                        metric.RepositoryId,
                        metric.PullRequestId,
                        metric.SealedOn,
                        metric.AddressedCount,
                        metric.AcknowledgedCount,
                        metric.DismissedCount,
                        metric.FalsePositiveCount,
                        metric.MissCount,
                        metric.DiscussedCount,
                    })
                    .ToListAsync(ct);

                return rows
                    .Select(row => new SealRow(
                        row.ClientId,
                        row.RepositoryId,
                        row.PullRequestId,
                        row.SealedOn,
                        row.AddressedCount,
                        row.AcknowledgedCount,
                        row.DismissedCount,
                        row.FalsePositiveCount,
                        row.MissCount,
                        row.DiscussedCount))
                    .ToList();
            },
            ct);
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

    private readonly record struct SealRow(
        Guid ClientId,
        string RepositoryId,
        long PullRequestId,
        DateOnly SealedOn,
        int Addressed,
        int Acknowledged,
        int Dismissed,
        int FalsePositive,
        int Misses,
        int Discussed);

    private readonly record struct ScopeKey(Guid ClientId, string? RepositoryId, long? PullRequestId);

    private readonly record struct ModelKey(string? ModelId, string? LogicalModelName);

    private readonly record struct ModelOutcomeRow(
        string? ModelId,
        string? LogicalModelName,
        CodeInsightDisposition Disposition);

    private readonly record struct OutcomeBucket(
        DateOnly BucketStart,
        IReadOnlyList<KeyValuePair<string, int>> Counts);
}
