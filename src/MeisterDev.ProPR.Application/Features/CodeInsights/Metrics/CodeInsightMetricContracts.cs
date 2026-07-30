// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.CodeInsights.Rollups;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.CodeInsights.Metrics;

/// <summary>
///     One measured result: the ratios, the counts they came from, and how much evidence stands behind them.
/// </summary>
/// <remarks>
///     <paramref name="SampleSize" /> is part of the contract rather than an afterthought. A view that draws a
///     confident line through two data points is worse than one that says there is not enough data yet, and it
///     cannot tell the difference without being told the sample.
/// </remarks>
/// <param name="Metrics">The ratios, with the inputs they were derived from.</param>
/// <param name="SampleSize">
///     How much the result rests on: sealed pull requests for a correctness result, resolved findings for an
///     acceptance one.
/// </param>
public sealed record CodeInsightMetricResult(CodeInsightMetrics Metrics, int SampleSize);

/// <summary>One measured result for one time bucket, when a read is a series.</summary>
/// <param name="BucketStart">Start of the bucket: the day, the week's Monday, or the month's first.</param>
/// <param name="Result">The measured result for this bucket.</param>
public sealed record CodeInsightMetricSeriesPoint(DateOnly BucketStart, CodeInsightMetricResult Result);

/// <summary>One measured result for one scope, when a read is grouped by grain.</summary>
/// <param name="ClientId">The client the scope belongs to.</param>
/// <param name="RepositoryId">Repository, when the grain includes one.</param>
/// <param name="PullRequestId">Pull request, when the grain includes one.</param>
/// <param name="Result">The measured result for this scope.</param>
/// <param name="RepositoryName">The repository's display name, when one has been recorded.</param>
public sealed record CodeInsightScopedMetricResult(
    Guid ClientId,
    string? RepositoryId,
    long? PullRequestId,
    CodeInsightMetricResult Result,
    string? RepositoryName = null);

/// <summary>
///     One measured result for one model, when reviewer performance is grouped by what produced the findings
///     rather than by where they landed.
/// </summary>
/// <remarks>
///     Both identities travel, and a row is one distinct pair. A logical model name is what an operator configures
///     and can repoint at another remote model; grouping on the name alone would merge two models' results under
///     one label, and on the remote id alone would lose the name being compared. A row with neither is the
///     unattributed remainder: findings collected before models were recorded, and findings no single pass owns.
/// </remarks>
/// <param name="ModelId">The remote model, or <see langword="null" /> when it was not recorded.</param>
/// <param name="LogicalModelName">The client's logical model name, when the pass ran through one.</param>
/// <param name="Result">
///     The attributable part of the measurement: precision and acceptance, with recall and F1 undefined because a
///     miss has no producing model.
/// </param>
public sealed record CodeInsightModelMetricResult(
    string? ModelId,
    string? LogicalModelName,
    CodeInsightMetricResult Result);

/// <summary>
///     How many rejections carried each reason, over a window and scope.
/// </summary>
/// <remarks>
///     <para>
///         A precision number says how often the reviewer was turned down. This says what to do about it, and the
///         answers do not overlap: a reviewer that invents problems needs a better prompt, one that argues with
///         deliberate decisions needs the codebase's conventions, one that repeats another tool needs to be told
///         what that tool covers.
///     </para>
///     <para>
///         <paramref name="Unclassified" /> is reported rather than folded into any reason. It counts rejections
///         whose reason could not be judged and rejections decided before reasons were recorded, and calling
///         either of those a reason would put a number nobody established into a distribution.
///     </para>
/// </remarks>
/// <param name="Counts">
///     One entry per reason present, with its count. A reason with no rejections is absent rather than zero, so a
///     caller decides for itself whether to draw an empty row.
/// </param>
/// <param name="Unclassified">Rejections carrying no reason.</param>
/// <param name="Rejections">Every rejection in scope, whether or not it carries a reason.</param>
/// <param name="ByConcernClass">
///     The same rejections split by what kind of concern they raised. Reported alongside the combined
///     distribution rather than instead of it, because the combined one answers "how often" and this one answers
///     "about what".
/// </param>
public sealed record CodeInsightRejectionReasonBreakdown(
    IReadOnlyDictionary<CodeInsightRejectionReason, int> Counts,
    int Unclassified,
    int Rejections,
    IReadOnlyList<CodeInsightConcernClassRejections> ByConcernClass)
{
    /// <summary>Nothing was rejected in the window, which is different from nothing being classified.</summary>
    public static CodeInsightRejectionReasonBreakdown Empty { get; } =
        new(new Dictionary<CodeInsightRejectionReason, int>(), 0, 0, []);
}

/// <summary>
///     One concern class and why its findings were turned down.
/// </summary>
/// <remarks>
///     The interesting comparison is within a class rather than across the whole set. Empirical work on AI review
///     feedback found functional and evolvability findings rejected at similar rates for entirely different
///     reasons, and a single distribution averages that difference away.
/// </remarks>
/// <param name="ConcernClass">
///     The class, or <see langword="null" /> for the findings that carry no core type and so belong to neither.
/// </param>
/// <param name="Counts">One entry per reason present in this class.</param>
/// <param name="WithoutReason">Rejections in this class whose reason could not be judged or was never recorded.</param>
/// <param name="Rejections">Every rejection in this class.</param>
public sealed record CodeInsightConcernClassRejections(
    CodeInsightConcernClass? ConcernClass,
    IReadOnlyDictionary<CodeInsightRejectionReason, int> Counts,
    int WithoutReason,
    int Rejections);
