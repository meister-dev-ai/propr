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
    Task<EvidenceBundle> CollectEvidenceAsync(
        VerificationWorkItem workItem,
        IReviewContextTools? reviewTools,
        string sourceBranch,
        CancellationToken ct = default);
}
