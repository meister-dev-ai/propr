// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Executes one fixture-backed review workflow without provider publication.
/// </summary>
public interface IReviewWorkflowRunner
{
    /// <summary>
    ///     Runs the review workflow for the supplied offline execution request.
    /// </summary>
    Task<ReviewWorkflowResult> RunAsync(ReviewWorkflowRequest request, CancellationToken cancellationToken = default);
}
