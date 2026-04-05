// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.DTOs;

/// <summary>
///     Carries review job data across the Application/Infrastructure boundary for API responses.
/// </summary>
/// <param name="Id">Unique identifier for the review job.</param>
/// <param name="ClientId">Identifier of the client that owns this job.</param>
/// <param name="OrganizationUrl">Azure DevOps organisation URL.</param>
/// <param name="ProjectId">Project identifier in the organisation.</param>
/// <param name="RepositoryId">Repository identifier.</param>
/// <param name="PullRequestId">Pull request identifier.</param>
/// <param name="IterationId">Iteration identifier within the pull request.</param>
/// <param name="Status">Current status of the job.</param>
/// <param name="SubmittedAt">When the job was submitted.</param>
/// <param name="ProcessingStartedAt">When the job began processing, if applicable.</param>
/// <param name="CompletedAt">When the job completed, if applicable.</param>
/// <param name="ErrorMessage">Error message if the job failed.</param>
/// <param name="TotalInputTokens">Total input tokens consumed across all AI calls in this review, from the protocol record.</param>
/// <param name="TotalOutputTokens">Total output tokens generated across all AI calls in this review, from the protocol record.</param>
public sealed record ReviewJobDto(
    Guid Id,
    Guid ClientId,
    string OrganizationUrl,
    string ProjectId,
    string RepositoryId,
    int PullRequestId,
    int IterationId,
    JobStatus Status,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? ProcessingStartedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorMessage,
    long? TotalInputTokens,
    long? TotalOutputTokens);

/// <summary>
///     Represents a single tool invocation captured during an agentic review pass.
/// </summary>
/// <param name="ToolName">Name of the tool that was called.</param>
/// <param name="Arguments">Serialised arguments passed to the tool.</param>
/// <param name="Result">Serialised result returned by the tool.</param>
/// <param name="InvokedAt">UTC timestamp at which the tool was invoked.</param>
public sealed record ReviewToolCallDto(string ToolName, string Arguments, string Result, DateTimeOffset InvokedAt);

/// <summary>
///     Represents a confidence evaluation snapshot for a single review concern.
/// </summary>
/// <param name="Concern">A short description of the concern being evaluated.</param>
/// <param name="Score">Confidence score in the range 0–100.</param>
public sealed record ConfidenceSnapshotDto(string Concern, int Score);
