// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.Features.Reviewing.Intake.Dtos;
using MeisterProPR.Application.Features.Reviewing.Intake.Ports;
using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Features.Reviewing.Intake.Queries.GetReviewJobStatus;

/// <summary>Handles loading the status of a review intake job.</summary>
public sealed class GetReviewJobStatusHandler(IReviewJobIntakeStore intakeStore)
{
    /// <summary>Returns the status DTO for the requested job, or <see langword="null" /> when the job does not exist.</summary>
    public async Task<ReviewJobStatusDto?> HandleAsync(
        GetReviewJobStatusQuery query,
        CancellationToken cancellationToken = default)
    {
        var job = await intakeStore.GetByIdAsync(query.JobId, cancellationToken);
        if (job is null)
        {
            return null;
        }

        return new ReviewJobStatusDto(
            job.Id,
            job.Status,
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.PullRequestId,
            job.IterationId,
            job.SubmittedAt,
            job.CompletedAt,
            job.Result is null
                ? null
                : new ReviewJobResultDto(
                    job.Result.Summary,
                    job.Result.Comments
                        .Select(comment => new ReviewJobCommentDto(
                            comment.FilePath,
                            comment.LineNumber,
                            comment.Severity,
                            comment.Message))
                        .ToArray()),
            job.ErrorMessage)
        {
            ClientId = job.ClientId,
            Provider = job.Provider,
            Host = job.ProviderHost,
            Repository = job.RepositoryReference,
            CodeReview = job.CodeReviewReference,
            ReviewRevision = job.ReviewRevisionReference,
            ResolvedReviewStrategy = job.ReviewStrategy,
            StrategySelectionSource = job.ReviewStrategySelectionSource,
            ComparisonMode = job.ReviewComparisonMode,
            PublicationMode = job.ReviewPublicationMode,
            ComparisonGroupId = job.ComparisonGroupId,
            Workspace = ResolveWorkspace(job),
        };
    }

    private static ReviewJobWorkspaceStatusDto? ResolveWorkspace(ReviewJob job)
    {
        var events = job.Protocols.SelectMany(protocol => protocol.Events).ToList();
        var prepared = events.FirstOrDefault(evt => string.Equals(evt.Name, "local_workspace_prepared", StringComparison.Ordinal));
        var failed = events.FirstOrDefault(evt => string.Equals(evt.Name, "local_workspace_failed", StringComparison.Ordinal));
        var fallback = events.FirstOrDefault(evt => string.Equals(evt.Name, "local_workspace_fallback_applied", StringComparison.Ordinal));
        if (prepared is null && failed is null && fallback is null)
        {
            return null;
        }

        return new ReviewJobWorkspaceStatusDto(
            true,
            prepared is not null,
            fallback is not null,
            TryGetString(prepared?.OutputSummary, "workspaceKey"),
            TryGetString(failed?.OutputSummary, "stage"),
            TryGetString(failed?.OutputSummary, "code"),
            TryGetString(failed?.OutputSummary, "message"));
    }

    private static string? TryGetString(string? json, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(propertyName, out var property) &&
                property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}
