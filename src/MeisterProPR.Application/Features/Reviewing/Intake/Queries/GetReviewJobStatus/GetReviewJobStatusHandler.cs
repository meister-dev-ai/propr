// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Intake.Dtos;
using MeisterProPR.Application.Features.Reviewing.Intake.Ports;

namespace MeisterProPR.Application.Features.Reviewing.Intake.Queries.GetReviewJobStatus;

/// <summary>Handles loading the status of a review intake job.</summary>
public sealed class GetReviewJobStatusHandler(IReviewJobIntakeStore intakeStore)
{
    /// <summary>Returns the status DTO for the requested job, or <see langword="null" /> when the job does not exist.</summary>
    public async Task<ReviewJobStatusDto?> HandleAsync(GetReviewJobStatusQuery query, CancellationToken cancellationToken = default)
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
                        .Select(comment => new ReviewJobCommentDto(comment.FilePath, comment.LineNumber, comment.Severity, comment.Message))
                        .ToArray()),
            job.ErrorMessage);
    }
}
