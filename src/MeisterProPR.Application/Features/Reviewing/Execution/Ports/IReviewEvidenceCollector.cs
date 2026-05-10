// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>
///     Collects bounded supporting evidence for routed verification work.
/// </summary>
public interface IReviewEvidenceCollector
{
    /// <summary>
    ///     Collects bounded evidence for a routed verification work item.
    /// </summary>
    /// <param name="workItem">Work item that needs evidence.</param>
    /// <param name="reviewTools">Optional review-context tools available during collection.</param>
    /// <param name="sourceBranch">Source branch used to resolve repository context.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <returns>The collected evidence bundle.</returns>
    Task<EvidenceBundle> CollectEvidenceAsync(
        VerificationWorkItem workItem,
        IReviewContextTools? reviewTools,
        string sourceBranch,
        CancellationToken ct = default);
}
