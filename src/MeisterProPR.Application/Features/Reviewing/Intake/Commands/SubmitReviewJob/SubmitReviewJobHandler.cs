// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Intake.Dtos;
using MeisterProPR.Application.Features.Reviewing.Intake.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Application.Features.Reviewing.Intake.Commands.SubmitReviewJob;

/// <summary>Handles creation and deduplication of review intake jobs.</summary>
public sealed partial class SubmitReviewJobHandler(
    IReviewJobIntakeStore intakeStore,
    IReviewExecutionQueue executionQueue,
    ILogger<SubmitReviewJobHandler> logger,
    IPullRequestFetcher? pullRequestFetcher = null,
    ILicensingCapabilityService? licensingCapabilityService = null,
    IClientRegistry? clientRegistry = null)
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
            return ToResult(existing, true);
        }

        var resolvedProfileId = await this.ResolveReviewPipelineProfileIdAsync(command.ClientId, cancellationToken);
        request = request with { ResolvedReviewPipelineProfileId = resolvedProfileId };

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
            return ToResult(job, false);
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
                return ToResult(job, false);
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

        return ToResult(job, false);
    }

    private async Task<string> ResolveReviewPipelineProfileIdAsync(
        Guid clientId,
        CancellationToken cancellationToken)
    {
        string? configuredProfileId = null;
        if (clientRegistry is not null)
        {
            configuredProfileId = await clientRegistry.GetDefaultReviewPipelineProfileIdAsync(clientId, cancellationToken);
        }

        return string.IsNullOrWhiteSpace(configuredProfileId)
            ? ReviewPipelineProfileCatalog.FileByFileBalancedProfileId
            : configuredProfileId;
    }

    private static SubmitReviewJobResult ToResult(ReviewJob job, bool isDuplicate)
    {
        return new SubmitReviewJobResult(
            job.Id,
            job.Status,
            isDuplicate);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to fetch PR context for intake job {JobId}; continuing without PR context.")]
    private static partial void LogPrContextFetchFailed(ILogger logger, Guid jobId, Exception ex);
}
