// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>Dispatches a review job to the orchestrator selected by the job strategy snapshot.</summary>
public interface IReviewStrategyDispatcher
{
    Task<ReviewResult> ReviewAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext baseContext,
        CancellationToken ct,
        IChatClient? overrideClient = null,
        string? pipelineProfileId = null);
}
