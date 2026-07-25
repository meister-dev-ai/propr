// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;

namespace MeisterDev.ProPR.Infrastructure.Repositories;

/// <summary>
///     Default <see cref="IAiConnectionScopeGuard" />. A connection profile is owned either by a tenant directly
///     or by one of that tenant's clients; either way it may only be referenced from inside that same tenant.
///     A profile owned by one client stays referenceable from its own tenant, because sharing within a tenant is
///     the tenant's own business, but crossing a tenant boundary is always refused.
/// </summary>
public sealed class AiConnectionScopeGuard(IClientRegistry clients) : IAiConnectionScopeGuard
{
    public async Task<string?> ValidateAsync(
        AiConnectionDto connection,
        Guid referencingTenantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var owningTenantId = await this.ResolveOwningTenantIdAsync(connection, ct).ConfigureAwait(false);

        if (owningTenantId is null)
        {
            return $"connection '{connection.Id}' has no resolvable owning tenant, so it cannot be referenced.";
        }

        if (owningTenantId.Value != referencingTenantId)
        {
            return $"connection '{connection.Id}' belongs to a different tenant and cannot be referenced.";
        }

        return null;
    }

    private async Task<Guid?> ResolveOwningTenantIdAsync(AiConnectionDto connection, CancellationToken ct)
    {
        if (connection.TenantId is { } tenantId && tenantId != Guid.Empty)
        {
            return tenantId;
        }

        if (connection.ClientId is { } clientId && clientId != Guid.Empty)
        {
            var clientTenantId = await clients.GetTenantIdAsync(clientId, ct).ConfigureAwait(false);
            return clientTenantId == Guid.Empty ? null : clientTenantId;
        }

        return null;
    }
}
