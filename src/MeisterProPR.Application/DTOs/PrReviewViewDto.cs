// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.DTOs;

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
    IReadOnlyList<ContributingMemorySummaryDto> ContributedMemories);

/// <summary>Summary of a single review job within the PR view.</summary>
public sealed record PrJobSummaryDto(
    Guid JobId,
    JobStatus Status,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? CompletedAt,
    int? FindingCount,
    long? TotalInputTokens,
    long? TotalOutputTokens,
    IReadOnlyList<TokenBreakdownEntry> TokenBreakdown);

/// <summary>Summary of a thread memory record that originated from this pull request.</summary>
public sealed record ThreadMemorySummaryDto(
    Guid MemoryRecordId,
    int ThreadId,
    string? FilePath,
    string ResolutionSummaryExcerpt,
    MemorySource Source,
    DateTimeOffset StoredAt);

/// <summary>Summary of an external memory record that contributed to a review in this pull request.</summary>
public sealed record ContributingMemorySummaryDto(
    Guid MemoryRecordId,
    MemorySource Source,
    string? OriginRepositoryId,
    int? OriginPullRequestId,
    string? FilePath,
    string ResolutionSummaryExcerpt,
    double? MaxSimilarityScore);
