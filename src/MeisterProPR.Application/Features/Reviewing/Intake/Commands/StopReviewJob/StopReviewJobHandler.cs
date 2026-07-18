// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Features.Reviewing.Intake.Commands.StopReviewJob;

/// <summary>
///     Handles a manual stop of a running or queued review job. The job's persisted status is set to
///     <see cref="JobStatus.Stopped" /> — the cross-instance source of truth honoured by the review
///     pipeline's status checkpoints — and the process-local cancellation source is signalled so a job
///     executing on this instance is interrupted promptly rather than at the next checkpoint.
/// </summary>
public sealed partial class StopReviewJobHandler(
    IJobRepository jobs,
    IReviewJobCancellationRegistry cancellationRegistry,
    ILogger<StopReviewJobHandler> logger)
{
    /// <summary>Marks the job stopped and signals cancellation, unless it has already finished.</summary>
    public async Task<StopReviewJobResult> HandleAsync(
        StopReviewJobCommand command,
        CancellationToken cancellationToken = default)
    {
        var job = jobs.GetById(command.JobId);
        if (job is null)
        {
            return new StopReviewJobResult(StopReviewJobOutcome.NotFound);
        }

        if (job.Status is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Superseded or JobStatus.Stopped)
        {
            LogStopRejectedTerminal(logger, job.Id, job.Status);
            return new StopReviewJobResult(StopReviewJobOutcome.AlreadyFinished, job.ClientId);
        }

        // Persist Stopped first so the pipeline's status checkpoints abort the review even when it runs on
        // another instance; then signal the local cancellation source for prompt interruption on this one.
        // Use CancellationToken.None: once an administrator has requested the stop it must not be dropped if
        // their request connection aborts mid-write (which would also skip the cancellation signal below).
        await jobs.SetStoppedAsync(command.JobId, CancellationToken.None);
        cancellationRegistry.Cancel(command.JobId);
        LogStopped(logger, job.Id, job.PullRequestId);

        return new StopReviewJobResult(StopReviewJobOutcome.Stopped, job.ClientId);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Stopped review job {JobId} for PR #{PrId} on client-administrator request.")]
    private static partial void LogStopped(ILogger logger, Guid jobId, int prId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Refused to stop review job {JobId} because its status is {Status}, which is already terminal.")]
    private static partial void LogStopRejectedTerminal(ILogger logger, Guid jobId, JobStatus status);
}
