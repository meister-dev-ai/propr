// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs;

/// <summary>
///     Projected read model for one row of the review overview list. Carries only the scalar fields the
///     overview renders, with the summary read from the denormalized <c>result_summary</c> column and the
///     token totals summed in the database — so the query never materializes the <c>result_json</c> blob,
///     never includes protocol rows, and runs untracked.
/// </summary>
/// <param name="Id">Review job identifier.</param>
/// <param name="ClientId">Owning client identifier.</param>
/// <param name="OrganizationUrl">Provider scope path (organization URL).</param>
/// <param name="ProjectId">Provider project key.</param>
/// <param name="RepositoryId">Repository identifier.</param>
/// <param name="PullRequestId">Pull request identifier.</param>
/// <param name="IterationId">Pull request iteration identifier.</param>
/// <param name="Status">Current job status.</param>
/// <param name="SubmittedAt">When the job was submitted.</param>
/// <param name="ProcessingStartedAt">When processing started, if it has.</param>
/// <param name="CompletedAt">When the job completed, if it has.</param>
/// <param name="ResultSummary">Denormalized review summary; null until the result is finalized.</param>
/// <param name="ErrorMessage">Failure message, if the job failed.</param>
/// <param name="TotalInputTokens">Total input tokens (aggregate column, else summed protocol tokens, else 0).</param>
/// <param name="TotalOutputTokens">Total output tokens (aggregate column, else summed protocol tokens, else 0).</param>
/// <param name="PrTitle">Pull request title, if known.</param>
/// <param name="PrSourceBranch">Source branch display name, if known.</param>
/// <param name="PrTargetBranch">Target branch display name, if known.</param>
/// <param name="PrRepositoryName">Repository display name, if known.</param>
/// <param name="AiModel">Snapshotted AI model used for the review, if known.</param>
/// <param name="FilesReviewed">
///     Count of per-file results that reached a terminal successful state (the live numerator of the
///     "files reviewed" progress metric). Excludes excluded, failed, and carried-forward files.
/// </param>
/// <param name="FilesInScope">
///     In-scope changed files after exclusions, fixed at job start (the progress denominator);
///     null until dispatch planning runs.
/// </param>
/// <param name="TotalEstimatedCostUsd">Persisted per-job USD cost; null when the model had no configured pricing.</param>
/// <param name="CostIsApproximate">True when the cost relied on a fallback rate or mixes priced and unpriced tiers.</param>
public sealed record JobListPageItemDto(
    Guid Id,
    Guid? ClientId,
    string OrganizationUrl,
    string ProjectId,
    string RepositoryId,
    int PullRequestId,
    int IterationId,
    JobStatus Status,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? ProcessingStartedAt,
    DateTimeOffset? CompletedAt,
    string? ResultSummary,
    string? ErrorMessage,
    long TotalInputTokens,
    long TotalOutputTokens,
    string? PrTitle,
    string? PrSourceBranch,
    string? PrTargetBranch,
    string? PrRepositoryName,
    string? AiModel,
    int FilesReviewed,
    int? FilesInScope,
    decimal? TotalEstimatedCostUsd,
    bool CostIsApproximate);
