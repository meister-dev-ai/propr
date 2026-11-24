// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.Features.Reviewing.Intake.Commands.SubmitReviewByCoordinates;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Api.Features.Reviewing.Contracts;

/// <summary>Provider-neutral repository identity supplied by review intake clients.</summary>
public sealed record ReviewRepositoryRefDto(string ExternalRepositoryId, string OwnerOrNamespace, string ProjectPath);

/// <summary>Provider-neutral code review identity supplied by review intake clients.</summary>
public sealed record ReviewCodeReviewRefDto(
    [property: JsonRequired] CodeReviewPlatformKind Platform,
    string ExternalReviewId,
    [property: JsonRequired] int Number);

/// <summary>Provider-neutral review revision identity supplied by review intake clients.</summary>
public sealed record ReviewRevisionRefDto(
    string HeadSha,
    string BaseSha,
    string? StartSha,
    string? ProviderRevisionId,
    string? PatchIdentity);

/// <summary>Provider-neutral reviewer identity supplied by review intake clients.</summary>
public sealed record ReviewReviewerIdentityDto(
    string ExternalUserId,
    string Login,
    string DisplayName,
    [property: JsonRequired] bool IsBot);

/// <summary>Request payload for provider-neutral review intake.</summary>
public sealed record SubmitReviewRequest(
    ScmProvider? Provider,
    string? HostBaseUrl,
    ReviewRepositoryRefDto? Repository,
    ReviewCodeReviewRefDto? CodeReview,
    ReviewRevisionRefDto? ReviewRevision)
{
    /// <summary>Optional normalized reviewer identity preferred for publication and automation.</summary>
    public ReviewReviewerIdentityDto? RequestedReviewerIdentity { get; init; }
}

/// <summary>Request payload identifying a pull request by the coordinates ProPR already stores.</summary>
/// <remarks>
///     These are the values pull-request resolution returns. No commit identity appears here: the revision is
///     read from the provider when the request arrives, and that lets the same request start a first
///     review or a re-review after new commits.
///     <para>
///         All four are needed and none is marked required, so an incomplete request reaches the action's own
///         validation and is refused in one shape, instead of being rejected earlier by the JSON binder in a
///         different one.
///     </para>
/// </remarks>
/// <param name="ProviderScopePath">Scope path exactly as the covering configuration stores it.</param>
/// <param name="ProviderProjectKey">Project, workspace, or namespace key exactly as the covering configuration stores it.</param>
/// <param name="RepositoryId">Provider repository identity.</param>
/// <param name="PullRequestId">Pull request number as the provider numbers it.</param>
public sealed record SubmitReviewByCoordinatesRequest(
    string? ProviderScopePath,
    string? ProviderProjectKey,
    string? RepositoryId,
    int? PullRequestId);

/// <summary>Response returned by coordinate-addressed review submission, whatever the outcome.</summary>
/// <param name="Outcome">The named outcome, which the caller shows to the person who asked for the review.</param>
/// <param name="JobId">The review job to follow, when the request reached one.</param>
/// <param name="Reason">A sentence explaining a refusal.</param>
public sealed record ReviewByCoordinatesResponse(
    SubmitReviewByCoordinatesOutcome Outcome,
    Guid? JobId,
    string? Reason);

/// <summary>Response returned when a review job is accepted or a duplicate is found.</summary>
public sealed record ReviewJobAcceptedResponse(
    Guid JobId,
    string Status,
    ScmProvider Provider,
    string? HostBaseUrl,
    ReviewRepositoryRefDto? Repository,
    ReviewCodeReviewRefDto? CodeReview,
    ReviewRevisionRefDto? ReviewRevision);

/// <summary>Response returned when a failed review job is restarted.</summary>
/// <param name="JobId">Identifier of the newly-created pending review job.</param>
/// <param name="SourceJobId">Identifier of the failed job that was restarted.</param>
/// <param name="Status">Lower-cased status of the new job (e.g. <c>pending</c>).</param>
public sealed record ReviewJobRestartResponse(
    Guid JobId,
    Guid SourceJobId,
    string Status);

/// <summary>Response returned when a running or queued review job is stopped.</summary>
/// <param name="JobId">Identifier of the stopped review job.</param>
/// <param name="Status">Lower-cased status of the job (<c>stopped</c>).</param>
public sealed record ReviewJobStopResponse(
    Guid JobId,
    string Status);

/// <summary>Detailed status response for a review job.</summary>
public sealed record ReviewStatusResponse(
    Guid JobId,
    JobStatus Status,
    string ProviderScopePath,
    string ProviderProjectKey,
    string RepositoryId,
    int PullRequestId,
    int IterationId,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? CompletedAt,
    ReviewResultDto? Result,
    string? Error)
{
    /// <summary>Normalized provider family for the review job.</summary>
    public ScmProvider Provider { get; init; } = ScmProviderVocabulary.AzureDevOpsCompatibilityDefault;

    /// <summary>Normalized provider host base URL for the review job.</summary>
    public string? HostBaseUrl { get; init; }

    /// <summary>Normalized repository identity for the review job.</summary>
    public ReviewRepositoryRefDto? Repository { get; init; }

    /// <summary>Normalized code review identity for the review job.</summary>
    public ReviewCodeReviewRefDto? CodeReview { get; init; }

    /// <summary>Normalized review revision identity for the review job.</summary>
    public ReviewRevisionRefDto? ReviewRevision { get; init; }

    /// <summary>Local workspace visibility metadata when available.</summary>
    public ReviewWorkspaceStatusDto? Workspace { get; init; }
}

/// <summary>DTO representing local review workspace adoption and fallback visibility.</summary>
public sealed record ReviewWorkspaceStatusDto(
    bool Attempted,
    bool Prepared,
    bool FallbackApplied,
    string? WorkspaceKey,
    string? FailureStage,
    string? FailureCode,
    string? FailureMessage);

/// <summary>DTO representing the textual review result and comments.</summary>
public sealed record ReviewResultDto(string Summary, ReviewCommentDto[] Comments);

/// <summary>DTO for a single review comment.</summary>
/// <param name="FilePath">The repository-relative path of the file, or <see langword="null" /> for the pull request.</param>
/// <param name="LineNumber">The line within the file, or <see langword="null" /> when the comment names no line.</param>
/// <param name="Severity">The classification of the finding: error, warning, suggestion or info.</param>
/// <param name="Message">The comment text.</param>
/// <param name="OriginPassKind">The kind of review pass that produced the finding, when recorded.</param>
/// <param name="ChangedLineRelation">Where the line of the comment lies relative to the changed lines, when classified.</param>
/// <param name="OriginPassIndex">The 1-based index of the numbered review pass that produced the finding, when recorded.</param>
/// <param name="OriginPassLens">The specialist lens of the review pass that produced the finding, when it had one.</param>
/// <param name="RiskAssessment">The assessed impact, likelihood and confidence, or <see langword="null" /> when unassessed.</param>
/// <param name="FindingClassification">
///     The finding types and analytics axes that the producing review assigned: core types, custom tags, level,
///     qualifier and confidence. <see langword="null" /> for a finding without a classification.
/// </param>
/// <param name="RelatedLocations">
///     The other places where the review reported the same root cause, when this comment stands for a group of
///     findings. <see langword="null" /> for a comment that stands for one finding.
/// </param>
/// <param name="RootCauseGroupSize">
///     The number of findings of the root-cause group this comment stands for, including the finding at its own
///     location. <see langword="null" /> for a comment that stands for one finding.
/// </param>
public sealed record ReviewCommentDto(
    string? FilePath,
    int? LineNumber,
    CommentSeverity Severity,
    string Message,
    string? OriginPassKind = null,
    ReviewCommentScopeRelation? ChangedLineRelation = null,
    int? OriginPassIndex = null,
    string? OriginPassLens = null,
    FindingRiskAssessment? RiskAssessment = null,
    FindingTypeClassification? FindingClassification = null,
    IReadOnlyList<ReviewCommentLocation>? RelatedLocations = null,
    int? RootCauseGroupSize = null);

/// <summary>A finding that the review produced and held back from publication, with the reason it was held back.</summary>
/// <param name="ReasonCode">
///     The reason code of the step that held the finding back, for example <c>not_confirmed</c> from verification or
///     <c>restates_published_findings</c> for a synthesized finding whose supporting findings are all published.
/// </param>
/// <param name="Comment">The held-back finding.</param>
public sealed record WithheldReviewFindingDto(string ReasonCode, ReviewCommentDto Comment);
