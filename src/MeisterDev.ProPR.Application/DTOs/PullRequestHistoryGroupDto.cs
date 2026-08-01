// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>
///     One pull request's review history: every review run against it, with the rollups the overview shows
///     beside the pull request itself.
/// </summary>
/// <remarks>
///     The review history is read per pull request, not per job, so the page is drawn at that grain. Grouping
///     jobs after fetching them forces a caller to hold the whole history to render its first page; grouping
///     here bounds a request to the pull requests actually on screen.
/// </remarks>
/// <param name="ProviderScopePath">Provider scope (organization or host) the pull request belongs to.</param>
/// <param name="ProviderProjectKey">Provider project key.</param>
/// <param name="RepositoryId">Repository identifier.</param>
/// <param name="PullRequestId">Pull request number within the repository.</param>
/// <param name="ClientId">Owning client, taken from the most recent run.</param>
/// <param name="PrTitle">Pull request title as captured by the most recent run.</param>
/// <param name="PrRepositoryName">Repository display name as captured by the most recent run.</param>
/// <param name="PrSourceBranch">Source branch as captured by the most recent run.</param>
/// <param name="PrTargetBranch">Target branch as captured by the most recent run.</param>
/// <param name="LatestActivityAt">Most recent activity across the pull request's runs; the ordering key.</param>
/// <param name="TotalInputTokens">Input tokens summed across the runs.</param>
/// <param name="TotalOutputTokens">Output tokens summed across the runs.</param>
/// <param name="TotalEstimatedCostUsd">
///     Cost summed across the runs, or null when none of them is priced. Null rather than zero so an
///     unpriced pull request reads as unknown instead of free.
/// </param>
/// <param name="CostIsApproximate">
///     True when any run is approximate, or when the pull request mixes priced and unpriced runs so the
///     total covers only part of the work.
/// </param>
/// <param name="Jobs">Every run against this pull request, most recent first, with running work first.</param>
public sealed record PullRequestHistoryGroupDto(
    string ProviderScopePath,
    string ProviderProjectKey,
    string RepositoryId,
    int PullRequestId,
    Guid? ClientId,
    string? PrTitle,
    string? PrRepositoryName,
    string? PrSourceBranch,
    string? PrTargetBranch,
    DateTimeOffset LatestActivityAt,
    long TotalInputTokens,
    long TotalOutputTokens,
    decimal? TotalEstimatedCostUsd,
    bool CostIsApproximate,
    IReadOnlyList<JobListPageItemDto> Jobs);
