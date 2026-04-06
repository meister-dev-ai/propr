// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Api.Features.Reviewing.Contracts;

/// <summary>Response returned when a review job is accepted or a duplicate is found.</summary>
public sealed record ReviewJobAcceptedResponse(Guid JobId, string Status);

/// <summary>Detailed status response for a review job.</summary>
public sealed record ReviewStatusResponse(
    Guid JobId,
    JobStatus Status,
    string OrganizationUrl,
    string ProjectId,
    string RepositoryId,
    int PullRequestId,
    int IterationId,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? CompletedAt,
    ReviewResultDto? Result,
    string? Error);

/// <summary>DTO representing the textual review result and comments.</summary>
public sealed record ReviewResultDto(string Summary, ReviewCommentDto[] Comments);

/// <summary>DTO for a single review comment.</summary>
public sealed record ReviewCommentDto(string? FilePath, int? LineNumber, CommentSeverity Severity, string Message);
