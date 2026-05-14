// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution;

/// <summary>Routes review execution to the orchestrator selected by the job strategy snapshot.</summary>
public sealed class ReviewStrategyDispatcher(
    IFileByFileReviewOrchestrator fileByFileOrchestrator,
    IPrWideAgenticReviewOrchestrator? prWideAgenticReviewOrchestrator = null) : IReviewStrategyDispatcher
{
    /// <inheritdoc />
    public Task<ReviewResult> ReviewAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext baseContext,
        CancellationToken ct,
        IChatClient? overrideClient = null)
    {
        return job.ReviewStrategy switch
        {
            ReviewStrategy.PrWideAgentic when prWideAgenticReviewOrchestrator is not null =>
                prWideAgenticReviewOrchestrator.ReviewAsync(job, pr, baseContext, ct, overrideClient),
            _ => fileByFileOrchestrator.ReviewAsync(job, pr, baseContext, ct, overrideClient),
        };
    }
}
