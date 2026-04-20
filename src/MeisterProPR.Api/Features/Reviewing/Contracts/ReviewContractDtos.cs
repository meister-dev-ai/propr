// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Api.Features.Reviewing.Contracts;

/// <summary>Provider-neutral repository identity supplied by review intake clients.</summary>
public sealed record ReviewRepositoryRefDto(string ExternalRepositoryId, string OwnerOrNamespace, string ProjectPath);

/// <summary>Provider-neutral code review identity supplied by review intake clients.</summary>
public sealed record ReviewCodeReviewRefDto(CodeReviewPlatformKind Platform, string ExternalReviewId, int Number);

/// <summary>Provider-neutral review revision identity supplied by review intake clients.</summary>
public sealed record ReviewRevisionRefDto(
    string HeadSha,
    string BaseSha,
    string? StartSha,
    string? ProviderRevisionId,
    string? PatchIdentity);

/// <summary>Provider-neutral reviewer identity supplied by review intake clients.</summary>
public sealed record ReviewReviewerIdentityDto(string ExternalUserId, string Login, string DisplayName, bool IsBot);

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

/// <summary>Response returned when a review job is accepted or a duplicate is found.</summary>
public sealed record ReviewJobAcceptedResponse(
    Guid JobId,
    string Status,
    ScmProvider Provider,
    string? HostBaseUrl,
    ReviewRepositoryRefDto? Repository,
    ReviewCodeReviewRefDto? CodeReview,
    ReviewRevisionRefDto? ReviewRevision);

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
}

/// <summary>DTO representing the textual review result and comments.</summary>
public sealed record ReviewResultDto(string Summary, ReviewCommentDto[] Comments);

/// <summary>DTO for a single review comment.</summary>
public sealed record ReviewCommentDto(string? FilePath, int? LineNumber, CommentSeverity Severity, string Message);
