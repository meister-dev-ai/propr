// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Features.Reviewing.Execution.Strategies.Ports;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution;

/// <summary>Routes review execution to the orchestrator selected by the job strategy snapshot.</summary>
public sealed class ReviewStrategyDispatcher(
    IFileByFileReviewOrchestrator fileByFileOrchestrator,
    IAgenticFileByFileReviewOrchestrator? agenticFileByFileReviewOrchestrator = null,
    IPrWideAgenticReviewOrchestrator? prWideAgenticReviewOrchestrator = null,
    IReviewPipelineProfileProvider? pipelineProfileProvider = null) : IReviewStrategyDispatcher
{
    /// <inheritdoc />
    public Task<ReviewResult> ReviewAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext baseContext,
        CancellationToken ct,
        IChatClient? overrideClient = null,
        string? pipelineProfileId = null)
    {
        if (!string.IsNullOrWhiteSpace(pipelineProfileId) && pipelineProfileProvider is not null)
        {
            var hasMatchingProfile = pipelineProfileProvider
                .GetProfiles(job.ReviewStrategy)
                .Any(profile => string.Equals(profile.ProfileId, pipelineProfileId, StringComparison.Ordinal));

            if (!hasMatchingProfile)
            {
                throw new InvalidOperationException($"No Reviewing pipeline profile '{pipelineProfileId}' is registered for strategy '{job.ReviewStrategy}'.");
            }
        }

        return job.ReviewStrategy switch
        {
            ReviewStrategy.AgenticFileByFile when agenticFileByFileReviewOrchestrator is not null =>
                agenticFileByFileReviewOrchestrator.ReviewAsync(job, pr, baseContext, ct, overrideClient),
            ReviewStrategy.PrWideAgentic when prWideAgenticReviewOrchestrator is not null =>
                prWideAgenticReviewOrchestrator.ReviewAsync(job, pr, baseContext, ct, overrideClient),
            _ => fileByFileOrchestrator.ReviewAsync(job, pr, baseContext, ct, overrideClient),
        };
    }
}
