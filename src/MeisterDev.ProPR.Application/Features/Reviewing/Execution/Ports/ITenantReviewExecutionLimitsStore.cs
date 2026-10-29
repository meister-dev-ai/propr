// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

/// <summary>Reads and stores the limits a tenant sets on how its reviews run.</summary>
public interface ITenantReviewExecutionLimitsStore
{
    /// <summary>Returns the limits of the tenant, or null when the tenant does not exist.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="ct">The cancellation token.</param>
    Task<TenantReviewExecutionLimits?> GetAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    ///     Stores both limits of the tenant, so a null value clears that limit. When a value changes, the store adds
    ///     an entry with the old and the new values to the audit history of the tenant. Storing the values the
    ///     tenant already has changes nothing and adds no entry.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="limits">The limits to store.</param>
    /// <param name="actorUserId">The administrator who set the limits, when known.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The stored limits, or null when the tenant does not exist.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside the range a tenant can set.</exception>
    Task<TenantReviewExecutionLimits?> SetAsync(
        Guid tenantId,
        TenantReviewExecutionLimits limits,
        Guid? actorUserId,
        CancellationToken ct = default);
}
