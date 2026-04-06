// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.Reviewing.Intake.Dtos;

/// <summary>Application DTO representing the status and result of a review intake job.</summary>
public sealed record ReviewJobStatusDto(
    Guid JobId,
    JobStatus Status,
    string OrganizationUrl,
    string ProjectId,
    string RepositoryId,
    int PullRequestId,
    int IterationId,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? CompletedAt,
    ReviewJobResultDto? Result,
    string? Error);

/// <summary>Application DTO representing the textual review result and comments.</summary>
public sealed record ReviewJobResultDto(string Summary, IReadOnlyList<ReviewJobCommentDto> Comments);

/// <summary>Application DTO for a single review comment.</summary>
public sealed record ReviewJobCommentDto(string? FilePath, int? LineNumber, CommentSeverity Severity, string Message);
