// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Intake.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Features.Reviewing.Intake.Commands.RestartReviewJob;

/// <summary>
///     Handles manual restart of a failed or budget-blocked review job. Automatic re-review is suppressed to
///     avoid cost-inducing loops on deterministic failures and to keep budget recovery a deliberate operator
///     action; this explicit request clones the source job's coordinates into a fresh pending job and queues it
///     for execution. A budget-held or budget-exceeded source is retired first so the clone is not rejected as an
///     active duplicate.
/// </summary>
public sealed partial class RestartReviewJobHandler(
    IJobRepository jobs,
    IReviewExecutionQueue executionQueue,
    ILogger<RestartReviewJobHandler> logger)
{
    /// <summary>Creates a new pending review job cloned from the failed job, unless an active duplicate exists.</summary>
    public async Task<RestartReviewJobResult> HandleAsync(
        RestartReviewJobCommand command,
        CancellationToken cancellationToken = default)
    {
        var source = jobs.GetById(command.JobId);
        if (source is null)
        {
            return new RestartReviewJobResult(RestartReviewJobOutcome.NotFound);
        }

        if (source.Status is not (JobStatus.Failed or JobStatus.BudgetHeld or JobStatus.BudgetExceeded))
        {
            LogRestartRejectedNotFailed(logger, source.Id, source.Status);
            return new RestartReviewJobResult(RestartReviewJobOutcome.NotFailed, ClientId: source.ClientId);
        }

        if (source.Status is JobStatus.BudgetHeld or JobStatus.BudgetExceeded)
        {
            // A budget-blocked source is still considered live for its pull request, so retire it before adding
            // the restart; otherwise the clone would be rejected as an active duplicate at the same revision. Any
            // findings the source already produced are carried forward into the restart the same way a superseded
            // job's results are.
            await jobs.SetSupersededAsync(source.Id, cancellationToken);
        }

        var restarted = new ReviewJob(
            Guid.NewGuid(),
            source.ClientId,
            source.OrganizationUrl,
            source.ProjectId,
            source.RepositoryId,
            source.PullRequestId,
            source.IterationId);

        restarted.SetReviewPipelineProfile(source.ReviewPipelineProfileId);

        restarted.SetProviderReviewContext(source.CodeReviewReference);
        restarted.SetReviewRevision(source.ReviewRevisionReference);
        restarted.SetAiConfig(source.AiConnectionId, source.AiModel, source.ReviewTemperature);
        restarted.SetProCursorSourceScope(source.ProCursorSourceScopeMode, source.ProCursorSourceIds);
        restarted.SetPrContext(source.PrTitle, source.PrRepositoryName, source.PrSourceBranch, source.PrTargetBranch);

        var addResult = await jobs.TryAddIfNoActiveDuplicateAsync(restarted, cancellationToken);
        if (!addResult.WasAdded)
        {
            LogRestartDuplicateActiveJob(logger, source.Id, source.PullRequestId, source.IterationId);
            return new RestartReviewJobResult(RestartReviewJobOutcome.DuplicateActiveJob, ClientId: source.ClientId);
        }

        await executionQueue.EnqueueAsync(restarted.Id, cancellationToken);
        LogRestarted(logger, source.Id, restarted.Id, source.PullRequestId, source.IterationId);

        return new RestartReviewJobResult(
            RestartReviewJobOutcome.Restarted,
            restarted.Id,
            source.ClientId);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Restarted failed review job {SourceJobId} as new job {NewJobId} for PR #{PrId} iteration {IterationId}.")]
    private static partial void LogRestarted(ILogger logger, Guid sourceJobId, Guid newJobId, int prId, int iterationId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Refused to restart review job {SourceJobId} because its status is {Status}, not Failed.")]
    private static partial void LogRestartRejectedNotFailed(ILogger logger, Guid sourceJobId, JobStatus status);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Skipped restart of review job {SourceJobId} for PR #{PrId} iteration {IterationId} because an active job already exists.")]
    private static partial void LogRestartDuplicateActiveJob(ILogger logger, Guid sourceJobId, int prId, int iterationId);
}
