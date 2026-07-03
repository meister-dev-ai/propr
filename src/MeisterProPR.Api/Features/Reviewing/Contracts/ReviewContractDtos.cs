// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json.Serialization;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Api.Features.Reviewing.Contracts;

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

    /// <summary>Optional per-job review strategy override. Missing preserves the client default or file_by_file fallback.</summary>
    public ReviewStrategy? ReviewStrategy { get; init; }

    /// <summary>Optional comparison mode. Missing defaults to single.</summary>
    public ReviewComparisonMode ComparisonMode { get; init; } = ReviewComparisonMode.Single;

    /// <summary>Optional publication mode. Missing defaults to publish.</summary>
    public ReviewPublicationMode PublicationMode { get; init; } = ReviewPublicationMode.Publish;
}

/// <summary>Response returned when a review job is accepted or a duplicate is found.</summary>
public sealed record ReviewJobAcceptedResponse(
    Guid JobId,
    string Status,
    ScmProvider Provider,
    string? HostBaseUrl,
    ReviewRepositoryRefDto? Repository,
    ReviewCodeReviewRefDto? CodeReview,
    ReviewRevisionRefDto? ReviewRevision)
{
    /// <summary>Resolved review strategy snapshotted on the accepted job.</summary>
    public ReviewStrategy ResolvedReviewStrategy { get; init; } = ReviewStrategy.FileByFile;

    /// <summary>How the resolved strategy was selected.</summary>
    public ReviewStrategySelectionSource StrategySelectionSource { get; init; } = ReviewStrategySelectionSource.FallbackDefault;

    /// <summary>Comparison mode snapshotted on the accepted job.</summary>
    public ReviewComparisonMode ComparisonMode { get; init; } = ReviewComparisonMode.Single;

    /// <summary>Publication mode snapshotted on the accepted job.</summary>
    public ReviewPublicationMode PublicationMode { get; init; } = ReviewPublicationMode.Publish;

    /// <summary>Comparison group identifier when this job participates in one.</summary>
    public Guid? ComparisonGroupId { get; init; }
}

/// <summary>Response returned when a failed review job is restarted.</summary>
/// <param name="JobId">Identifier of the newly-created pending review job.</param>
/// <param name="SourceJobId">Identifier of the failed job that was restarted.</param>
/// <param name="Status">Lower-cased status of the new job (e.g. <c>pending</c>).</param>
public sealed record ReviewJobRestartResponse(
    Guid JobId,
    Guid SourceJobId,
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
    public ScmProvider Provider { get; init; } = ScmProvider.AzureDevOps;

    /// <summary>Normalized provider host base URL for the review job.</summary>
    public string? HostBaseUrl { get; init; }

    /// <summary>Normalized repository identity for the review job.</summary>
    public ReviewRepositoryRefDto? Repository { get; init; }

    /// <summary>Normalized code review identity for the review job.</summary>
    public ReviewCodeReviewRefDto? CodeReview { get; init; }

    /// <summary>Normalized review revision identity for the review job.</summary>
    public ReviewRevisionRefDto? ReviewRevision { get; init; }

    /// <summary>Resolved review strategy snapshotted on the job.</summary>
    public ReviewStrategy ResolvedReviewStrategy { get; init; } = ReviewStrategy.FileByFile;

    /// <summary>How the resolved strategy was selected.</summary>
    public ReviewStrategySelectionSource StrategySelectionSource { get; init; } = ReviewStrategySelectionSource.FallbackDefault;

    /// <summary>Comparison mode snapshotted on the job.</summary>
    public ReviewComparisonMode ComparisonMode { get; init; } = ReviewComparisonMode.Single;

    /// <summary>Publication mode snapshotted on the job.</summary>
    public ReviewPublicationMode PublicationMode { get; init; } = ReviewPublicationMode.Publish;

    /// <summary>Comparison group identifier when this job participates in one.</summary>
    public Guid? ComparisonGroupId { get; init; }

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
public sealed record ReviewCommentDto(
    string? FilePath,
    int? LineNumber,
    CommentSeverity Severity,
    string Message,
    string? OriginPassKind = null,
    ReviewCommentScopeRelation? ChangedLineRelation = null);
