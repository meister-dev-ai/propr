// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Strategies.Ports;

/// <summary>Runs the staged plan-driven per-file review strategy.</summary>
public interface IAgenticFileByFileReviewOrchestrator
{
    /// <summary>
    ///     Executes the agentic file-by-file review strategy for a given pull request, orchestrating the generation of file-scoped review plans, conducting bounded
    ///     investigations for identified concerns,
    ///     and synthesizing the results into a comprehensive review outcome.
    /// </summary>
    /// <param name="job">The review job containing the context and parameters for the review.</param>
    /// <param name="pr">The pull request to be reviewed.</param>
    /// <param name="baseContext">The base context for the review system.</param>
    /// <param name="ct">Cancellation token to cancel the review operation.</param>
    /// <param name="overrideClient">Optional chat client to override the default client.</param>
    /// <returns>A task representing the asynchronous review operation, containing the review result.</returns>
    Task<ReviewResult> ReviewAsync(
        ReviewJob job,
        PullRequest pr,
        ReviewSystemContext baseContext,
        CancellationToken ct,
        IChatClient? overrideClient = null);
}
