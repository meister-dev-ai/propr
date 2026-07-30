// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.CodeInsights.Rollups;

/// <summary>
///     Reads the projected counts. Grain and bucket size are parameters rather than separate methods, because
///     the stored rows already carry every scope part as a real column.
/// </summary>
public interface ICodeInsightRollupReader
{
    /// <summary>
    ///     Returns a counted series for one dimension over the window, bucketed as asked.
    ///     For <see cref="CodeInsightCountDimension.CoreType" /> the series is comparable across clients; the
    ///     projection holds no custom types precisely so that a cross-client read cannot accidentally mix them.
    /// </summary>
    Task<IReadOnlyList<CodeInsightSeriesPoint>> GetSeriesAsync(
        CodeInsightRollupQuery query,
        CodeInsightCountDimension dimension,
        CodeInsightBucketSize bucketSize,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns the top <paramref name="topN" /> scopes by finding count at the requested grain: the
    ///     "where do findings cluster" question.
    /// </summary>
    Task<IReadOnlyList<CodeInsightConcentrationRow>> GetConcentrationAsync(
        CodeInsightRollupQuery query,
        CodeInsightGrain grain,
        int topN,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns the total finding count in the window at the requested grain, as a single number per scope.
    ///     Used to reconcile grains against one another and to size a metric's sample.
    /// </summary>
    Task<int> GetTotalAsync(CodeInsightRollupQuery query, CancellationToken ct = default);

    /// <summary>
    ///     Returns every repository with findings in the window, busiest first, with the totals across them: what a
    ///     reader picks from before any per-repository number is worth reading.
    /// </summary>
    Task<CodeInsightRepositoryDirectory> GetRepositoryDirectoryAsync(
        CodeInsightRollupQuery query,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns each file's history (findings, the pull requests that raised them, and the average per such
    ///     pull request) worst first, with the totals those rows sit inside.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Read across pull requests by definition, so any pull-request filter on <paramref name="query" /> is
    ///         ignored rather than honoured: a hotspot computed inside one pull request would just be that pull
    ///         request's findings under a name promising history.
    ///     </para>
    ///     <para>
    ///         <paramref name="filesFromPullRequestId" /> selects <em>which files</em> to report on (the ones that
    ///         pull request raised findings in) and never which findings to count. That is what lets a view
    ///         embedded in a review say "this file has produced thirty findings before today".
    ///     </para>
    ///     <para>
    ///         Grouped by symbol, the rows are definitions within their files and only findings the syntax placed
    ///         are counted; the remainder is reported as <c>UnplacedFindings</c> rather than ranked as a bucket.
    ///     </para>
    /// </remarks>
    Task<CodeInsightHotspotReport> GetHotspotsAsync(
        CodeInsightRollupQuery query,
        long? filesFromPullRequestId,
        int topN,
        CodeInsightHotspotGrouping grouping = CodeInsightHotspotGrouping.File,
        CancellationToken ct = default);
}
