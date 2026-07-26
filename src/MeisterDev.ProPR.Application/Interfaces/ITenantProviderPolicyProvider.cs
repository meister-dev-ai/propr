// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.AI;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Reads a tenant's provider-kind policy. Deliberately not cached: the acceptance the policy exists for
///     includes a change taking effect, and a stale allow-list is a tenant still using a provider it has just
///     forbidden.
/// </summary>
public interface ITenantProviderPolicyProvider
{
    /// <summary>Returns the policy for <paramref name="tenantId" />, unrestricted when none is stated.</summary>
    /// <param name="tenantId">The tenant whose policy to read.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TenantProviderPolicy> GetForTenantAsync(Guid tenantId, CancellationToken ct = default);

    /// <summary>
    ///     Returns the policy of the tenant owning <paramref name="clientId" />, unrestricted when the client or
    ///     its tenant cannot be found — a lookup failure must not silently forbid every provider and stop reviews.
    /// </summary>
    /// <param name="clientId">The client whose owning tenant's policy to read.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TenantProviderPolicy> GetForClientAsync(Guid clientId, CancellationToken ct = default);
}
