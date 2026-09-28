// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Reads a tenant's reasoning-capture policy. Not cached: a controller who turns capture off expects the next
///     job to honour it, and a stale value is a job still writing reasoning the tenant has just forbidden.
/// </summary>
public interface ITenantReasoningCapturePolicyProvider
{
    /// <summary>
    ///     Returns the policy of the tenant owning <paramref name="clientId" />.
    ///     <see cref="ReasoningCapturePolicy.InstallationDefault" /> is returned for an unknown client, a client
    ///     whose tenant cannot be found, and a failed lookup, which is logged at Warning: a lookup failure must
    ///     leave the installation switch in charge instead of changing what a review records.
    /// </summary>
    /// <param name="clientId">The client whose owning tenant's policy to read.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ReasoningCapturePolicy> GetForClientAsync(Guid clientId, CancellationToken ct = default);
}
