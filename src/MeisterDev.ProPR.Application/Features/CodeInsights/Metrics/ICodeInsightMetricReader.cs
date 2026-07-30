// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.CodeInsights.Rollups;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.CodeInsights.Metrics;

/// <summary>
///     Reads the two metric lenses.
/// </summary>
/// <remarks>
///     The two lenses come from different places on purpose. Correctness is read from sealed per-pull-request
///     snapshots, because it needs a moment at which the answer stopped changing. Acceptance is read from the
///     live count projection, because it is the early signal: it has to be available on the first day, long
///     before any pull request has closed.
/// </remarks>
public interface ICodeInsightMetricReader
{
    /// <summary>
    ///     Returns the correctness lens over the pull requests that were sealed inside the query's window and
    ///     fall inside its scope, computed by summing their stored inputs and dividing once.
    /// </summary>
    Task<CodeInsightMetricResult> GetCorrectnessAsync(
        CodeInsightRollupQuery query,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns the correctness lens grouped by <paramref name="grain" />: how a pull-request-level
    ///     measurement rolls up to a repository or a client. Only the scope grains a seal has: a seal is
    ///     per-pull-request, so file and job grains are not meaningful here and are treated as the pull-request
    ///     grain.
    /// </summary>
    Task<IReadOnlyList<CodeInsightScopedMetricResult>> GetCorrectnessByGrainAsync(
        CodeInsightRollupQuery query,
        CodeInsightGrain grain,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns the correctness lens as a series over the window, one point per bucket, so a trend and its
    ///     direction can be read. A bucket holds the pull requests sealed inside it, and carries its own sample
    ///     size, which is what lets a view refuse to draw a confident line through two closed pull requests.
    /// </summary>
    Task<IReadOnlyList<CodeInsightMetricSeriesPoint>> GetCorrectnessSeriesAsync(
        CodeInsightRollupQuery query,
        CodeInsightBucketSize bucketSize,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns the acceptance lens over the query's window and scope, from the recorded outcomes. Needs no
    ///     seal and no closed pull request: available as soon as the first finding has resolved.
    /// </summary>
    Task<CodeInsightMetricResult> GetAcceptanceAsync(
        CodeInsightRollupQuery query,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns the acceptance lens as a series over the window, one point per bucket. Buckets follow review
    ///     date, so a period's acceptance keeps maturing as its findings resolve.
    /// </summary>
    Task<IReadOnlyList<CodeInsightMetricSeriesPoint>> GetAcceptanceSeriesAsync(
        CodeInsightRollupQuery query,
        CodeInsightBucketSize bucketSize,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns what each model produced and what became of it: the reading that answers whether a cheaper
    ///     model would have done.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Read from the findings themselves rather than from the seals, because a seal is per pull request and
    ///         a pull request can be reviewed by several models; there is nothing in it to split by model. That
    ///         also means this read needs no closed pull request: it is available as soon as findings resolve.
    ///     </para>
    ///     <para>
    ///         The sample is resolved findings, not pull requests, and only the attributable lenses are computed.
    ///         See <see cref="CodeInsightMetricCalculator.ComputeAttributable" /> for why a miss cannot be charged
    ///         to a model.
    ///     </para>
    /// </remarks>
    Task<IReadOnlyList<CodeInsightModelMetricResult>> GetByModelAsync(
        CodeInsightRollupQuery query,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns why the rejections in the window were rejected. Read from the recorded outcomes like the
    ///     acceptance lens, so it needs no seal and no closed pull request.
    /// </summary>
    Task<CodeInsightRejectionReasonBreakdown> GetRejectionReasonsAsync(
        CodeInsightRollupQuery query,
        CancellationToken ct = default);
}
