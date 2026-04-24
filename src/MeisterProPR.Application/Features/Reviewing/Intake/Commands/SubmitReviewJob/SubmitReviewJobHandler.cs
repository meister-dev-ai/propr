// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;
using MeisterProPR.Application.Features.Reviewing.Intake.Ports;
using MeisterProPR.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Features.Reviewing.Intake.Commands.SubmitReviewJob;

/// <summary>Handles creation and deduplication of review intake jobs.</summary>
public sealed partial class SubmitReviewJobHandler(
    IReviewJobIntakeStore intakeStore,
    IReviewExecutionQueue executionQueue,
    ILogger<SubmitReviewJobHandler> logger,
    IPullRequestFetcher? pullRequestFetcher = null,
    ILicensingCapabilityService? licensingCapabilityService = null)
{
    /// <summary>Creates a new review job unless an active job already exists for the requested PR iteration.</summary>
    public async Task<SubmitReviewJobResult> HandleAsync(
        SubmitReviewJobCommand command,
        CancellationToken cancellationToken = default)
    {
        var request = command.Request;

        var existing = await intakeStore.FindActiveJobAsync(
            command.ClientId,
            request,
            cancellationToken);

        if (existing is not null)
        {
            return new SubmitReviewJobResult(existing.Id, existing.Status, true);
        }

        if (licensingCapabilityService is not null)
        {
            var parallelExecutionCapability = await licensingCapabilityService.GetCapabilityAsync(
                PremiumCapabilityKey.ParallelReviewExecution,
                cancellationToken);

            if (!parallelExecutionCapability.IsAvailable)
            {
                var activeJobCount = await intakeStore.CountActiveJobsAsync(cancellationToken);
                if (activeJobCount > 0)
                {
                    throw new PremiumFeatureUnavailableException(parallelExecutionCapability);
                }
            }
        }

        var job = await intakeStore.CreatePendingJobAsync(command.ClientId, request, cancellationToken);
        await executionQueue.EnqueueAsync(job.Id, cancellationToken);

        if (pullRequestFetcher is null)
        {
            return new SubmitReviewJobResult(job.Id, job.Status, false);
        }

        try
        {
            var pr = await pullRequestFetcher.FetchAsync(
                request.ProviderScopePath,
                request.ProviderProjectKey,
                request.RepositoryId,
                request.PullRequestId,
                request.IterationId,
                clientId: command.ClientId,
                cancellationToken: cancellationToken);

            if (pr is null)
            {
                return new SubmitReviewJobResult(job.Id, job.Status, false);
            }

            await intakeStore.UpdatePrContextAsync(
                job.Id,
                pr.Title,
                pr.RepositoryName,
                pr.SourceBranch,
                pr.TargetBranch,
                cancellationToken);
        }
        catch (Exception ex)
        {
            LogPrContextFetchFailed(logger, job.Id, ex);
        }

        return new SubmitReviewJobResult(job.Id, job.Status, false);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to fetch PR context for intake job {JobId}; continuing without PR context.")]
    private static partial void LogPrContextFetchFailed(ILogger logger, Guid jobId, Exception ex);
}
