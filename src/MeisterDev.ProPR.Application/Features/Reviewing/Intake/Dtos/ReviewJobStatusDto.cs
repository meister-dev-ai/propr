// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Intake.Dtos;

/// <summary>Application DTO representing the status and result of a review intake job.</summary>
public sealed record ReviewJobStatusDto(
    Guid JobId,
    JobStatus Status,
    string ProviderScopePath,
    string ProviderProjectKey,
    string RepositoryId,
    int PullRequestId,
    int IterationId,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? CompletedAt,
    ReviewJobResultDto? Result,
    string? Error)
{
    /// <summary>Normalized SCM provider family for the stored review job.</summary>
    public Guid ClientId { get; init; }

    /// <summary>Normalized SCM provider family for the stored review job.</summary>
    public required ScmProvider Provider { get; init; }

    /// <summary>Normalized provider host reference when available.</summary>
    public ProviderHostRef? Host { get; init; }

    /// <summary>Normalized repository reference when available.</summary>
    public RepositoryRef? Repository { get; init; }

    /// <summary>Normalized review reference when available.</summary>
    public CodeReviewRef? CodeReview { get; init; }

    /// <summary>Normalized review revision when available.</summary>
    public ReviewRevision? ReviewRevision { get; init; }

    /// <summary>Local workspace visibility metadata when available.</summary>
    public ReviewJobWorkspaceStatusDto? Workspace { get; init; }
}

/// <summary>Application DTO representing local workspace adoption and fallback visibility.</summary>
public sealed record ReviewJobWorkspaceStatusDto(
    bool Attempted,
    bool Prepared,
    bool FallbackApplied,
    string? WorkspaceKey,
    string? FailureStage,
    string? FailureCode,
    string? FailureMessage);

/// <summary>Application DTO representing the textual review result and comments.</summary>
public sealed record ReviewJobResultDto(string Summary, IReadOnlyList<ReviewJobCommentDto> Comments);

/// <summary>Application DTO for a single review comment.</summary>
/// <param name="FilePath">The repository-relative path of the file, or <see langword="null" /> for the pull request.</param>
/// <param name="LineNumber">The line within the file, or <see langword="null" /> when the comment names no line.</param>
/// <param name="Severity">The classification of the finding.</param>
/// <param name="Message">The comment text.</param>
/// <param name="RiskAssessment">The assessed impact, likelihood and confidence, or <see langword="null" /> when unassessed.</param>
/// <param name="FindingClassification">
///     The finding types and analytics axes that the producing review assigned, or <see langword="null" /> for a finding
///     without a classification.
/// </param>
/// <param name="RelatedLocations">
///     The other places where the review reported the same root cause, or <see langword="null" /> for a comment that
///     stands for one finding.
/// </param>
/// <param name="RootCauseGroupSize">
///     The number of findings of the root-cause group this comment stands for, or <see langword="null" /> for a comment
///     that stands for one finding.
/// </param>
public sealed record ReviewJobCommentDto(
    string? FilePath,
    int? LineNumber,
    CommentSeverity Severity,
    string Message,
    FindingRiskAssessment? RiskAssessment = null,
    FindingTypeClassification? FindingClassification = null,
    IReadOnlyList<ReviewCommentLocation>? RelatedLocations = null,
    int? RootCauseGroupSize = null);
