// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Intake.Dtos;

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
    public ScmProvider Provider { get; init; } = ScmProvider.AzureDevOps;

    /// <summary>Normalized provider host reference when available.</summary>
    public ProviderHostRef? Host { get; init; }

    /// <summary>Normalized repository reference when available.</summary>
    public RepositoryRef? Repository { get; init; }

    /// <summary>Normalized review reference when available.</summary>
    public CodeReviewRef? CodeReview { get; init; }

    /// <summary>Normalized review revision when available.</summary>
    public ReviewRevision? ReviewRevision { get; init; }
}

/// <summary>Application DTO representing the textual review result and comments.</summary>
public sealed record ReviewJobResultDto(string Summary, IReadOnlyList<ReviewJobCommentDto> Comments);

/// <summary>Application DTO for a single review comment.</summary>
public sealed record ReviewJobCommentDto(string? FilePath, int? LineNumber, CommentSeverity Severity, string Message);
