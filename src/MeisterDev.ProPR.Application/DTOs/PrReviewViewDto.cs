// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>Aggregated view of all review activity for a specific pull request.</summary>
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
    bool CostIsApproximate = false);

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
    long ThreadId,
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
