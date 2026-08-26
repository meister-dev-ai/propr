// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>Answers a runner asking for work with a manifest or a typed reason there is none.</summary>
public interface IRunnerLeaseOfferService
{
    /// <summary>Offers this runner the highest-priority job it is allowed to take, if there is one.</summary>
    /// <param name="request">Who is asking, how much room it has, and which contract it speaks.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<RunnerLeaseOffer> OfferAsync(RunnerLeaseRequest request, CancellationToken ct = default);
}
