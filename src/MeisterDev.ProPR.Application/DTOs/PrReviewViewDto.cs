// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>Aggregated view of all review activity for a specific pull request.</summary>
/// <remarks>
///     <see cref="PendingReview" /> is what tells a surface to offer a review rather than only report past
///     ones. It is computed here rather than by each client, so the ProPR UI and the browser extension
///     cannot disagree about whether a pull request is waiting.
/// </remarks>
public sealed record PrReviewViewDto(
    string ProviderScopePath,
    string ProviderProjectKey,
    string RepositoryId,
    int PullRequestId,
    int TotalJobs,
    long TotalInputTokens,
    long TotalOutputTokens,
    IReadOnlyList<TokenBreakdownEntry> AggregatedTokenBreakdown,
    bool BreakdownConsistent,
    IReadOnlyList<PrJobSummaryDto> Jobs,
    int OriginatedMemoryCount,
    IReadOnlyList<ThreadMemorySummaryDto> OriginatedMemories,
    int ContributedMemoryCount,
    IReadOnlyList<ContributingMemorySummaryDto> ContributedMemories,
    decimal? TotalEstimatedCostUsd = null,
    bool CostIsApproximate = false,
    IReadOnlyList<PrThreadPassSummaryDto>? ThreadPasses = null,
    decimal? ThreadPassTotalEstimatedCostUsd = null,
    bool ThreadPassCostIsApproximate = false,
    PendingReviewDto? PendingReview = null);

/// <summary>
///     Says that the pull request has moved past the revision it was reviewed at, and was left there because
///     the client reviews only a pull request's first increment.
/// </summary>
/// <remarks>
///     Present only while that is true. Absent covers every other case with one answer, which is what a
///     surface deciding whether to offer the action needs: never reviewed, up to date, or reviewed at the
///     revision that was once pending. A caller that renders this must not re-derive it from the two revision
///     keys, because the rule for what counts as ahead lives here.
/// </remarks>
/// <param name="RevisionKey">The revision the pull request sits at, unreviewed.</param>
/// <param name="ReviewedRevisionKey">
///     The revision the files were last reviewed at, or null when no review has recorded one.
/// </param>
/// <param name="DetectedAt">When the unreviewed revision was first seen, so a reader can say how long ago.</param>
public sealed record PendingReviewDto(
    string RevisionKey,
    string? ReviewedRevisionKey,
    DateTimeOffset? DetectedAt);

/// <summary>Summary of one thread pass over this pull request's conversation.</summary>
/// <remarks>
///     Listed beside the review jobs rather than folded into them: the two run on separate cadences, and an
///     increment may carry one, the other, or both. The totals above stay the review jobs' own, so what a
///     reader already knows those numbers to mean does not change.
/// </remarks>
/// <param name="ThreadPassId">Identifier of the pass, and of its trace.</param>
/// <param name="Status">Where the pass ended up.</param>
/// <param name="CreatedAt">When the pass was queued.</param>
/// <param name="CompletedAt">When the pass reached a terminal status, if it has.</param>
/// <param name="ThreadCount">How many threads the pass acted on.</param>
/// <param name="TotalInputTokens">Input tokens the pass spent.</param>
/// <param name="TotalOutputTokens">Output tokens the pass spent.</param>
/// <param name="TotalEstimatedCostUsd">What the pass cost, or null when nothing priced was recorded.</param>
/// <param name="CostIsApproximate">True when some of the pass's spend had no known price.</param>
/// <param name="ErrorMessage">Why the last attempt failed, if it did.</param>
/// <param name="BudgetBlockScope">The budget scope that blocked the pass, if one did.</param>
/// <param name="BudgetBlockCapKind">Whether the cap that blocked the pass was soft or hard.</param>
/// <param name="BudgetBlockThresholdUsd">The cap the blocked scope was measured against.</param>
/// <param name="BudgetBlockSpentUsd">What the blocked scope had already spent.</param>
public sealed record PrThreadPassSummaryDto(
    Guid ThreadPassId,
    ThreadPassJobStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    int ThreadCount,
    long TotalInputTokens,
    long TotalOutputTokens,
    decimal? TotalEstimatedCostUsd,
    bool CostIsApproximate,
    string? ErrorMessage = null,
    BudgetScopeKind? BudgetBlockScope = null,
    BudgetCapKind? BudgetBlockCapKind = null,
    decimal? BudgetBlockThresholdUsd = null,
    decimal? BudgetBlockSpentUsd = null);

/// <summary>Summary of a single review job within the PR view.</summary>
public sealed record PrJobSummaryDto(
    Guid JobId,
    JobStatus Status,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? CompletedAt,
    int? FindingCount,
    long? TotalInputTokens,
    long? TotalOutputTokens,
    IReadOnlyList<TokenBreakdownEntry> TokenBreakdown,
    decimal? TotalEstimatedCostUsd = null,
    bool CostIsApproximate = false);

/// <summary>Summary of a thread memory record that originated from this pull request.</summary>
/// <param name="MemoryRecordId">Record identifier.</param>
/// <param name="ThreadId">Provider thread identifier.</param>
/// <param name="FilePath">File the thread was anchored to, if any.</param>
/// <param name="ResolutionSummaryExcerpt">Opening of the stored resolution summary.</param>
/// <param name="Source">Whether the record came from a resolved thread or an administrator dismissal.</param>
/// <param name="StoredAt">When the record was last written.</param>
/// <param name="ResolutionIntent">
///     What the reviewer's resolution meant: a rejection of the finding, or a claim that it was fixed.
///     <see langword="null" /> for a record written before the outcome was kept, and for an administrator
///     dismissal, which carries no reviewer decision.
/// </param>
/// <param name="ResolutionClarity">
///     How plainly the discussion stated the resolution. Distinguishes a rejection a reviewer made explicit
///     from one inferred from an unclear thread.
/// </param>
public sealed record ThreadMemorySummaryDto(
    Guid MemoryRecordId,
    string ThreadId,
    string? FilePath,
    string ResolutionSummaryExcerpt,
    MemorySource Source,
    DateTimeOffset StoredAt,
    ThreadResolutionIntent? ResolutionIntent = null,
    ResolutionClarity? ResolutionClarity = null);

/// <summary>Summary of an external memory record that contributed to a review in this pull request.</summary>
public sealed record ContributingMemorySummaryDto(
    Guid MemoryRecordId,
    MemorySource Source,
    string? OriginRepositoryId,
    int? OriginPullRequestId,
    string? FilePath,
    string ResolutionSummaryExcerpt,
    double? MaxSimilarityScore);
