// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Repositories;

/// <summary>
///     Reads the provider-kind allow-list off the tenant row.
/// </summary>
/// <remarks>
///     The system tenant has no allow-list surface, so it is answered as unrestricted without a query: a policy
///     nobody can edit could only ever be a trap. A stored name that no longer parses is dropped rather than
///     failing the read, which keeps a renamed or removed provider family from locking a tenant out of every
///     provider — the surviving names still restrict, and a policy that reduces to nothing reads as unrestricted.
/// </remarks>
public sealed class TenantProviderPolicyProvider(IDbContextFactory<MeisterProPRDbContext> contextFactory)
    : ITenantProviderPolicyProvider
{
    /// <inheritdoc />
    public async Task<TenantProviderPolicy> GetForTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty || TenantCatalog.IsSystemTenant(tenantId))
        {
            return TenantProviderPolicy.Unrestricted;
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var stored = await db.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => new StoredPolicy(tenant.AllowedAiProviderKinds, tenant.AllowedAiEndpointHosts))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return ToPolicy(stored);
    }

    /// <inheritdoc />
    public async Task<TenantProviderPolicy> GetForClientAsync(Guid clientId, CancellationToken ct = default)
    {
        if (clientId == Guid.Empty)
        {
            return TenantProviderPolicy.Unrestricted;
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var tenantId = await db.Clients
            .AsNoTracking()
            .Where(client => client.Id == clientId)
            .Select(client => client.TenantId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (tenantId == Guid.Empty || TenantCatalog.IsSystemTenant(tenantId))
        {
            return TenantProviderPolicy.Unrestricted;
        }

        var stored = await db.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => new StoredPolicy(tenant.AllowedAiProviderKinds, tenant.AllowedAiEndpointHosts))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return ToPolicy(stored);
    }

    private static TenantProviderPolicy ToPolicy(StoredPolicy? stored)
    {
        if (stored is null)
        {
            return TenantProviderPolicy.Unrestricted;
        }

        var kinds = (stored.ProviderKinds ?? [])
            .Select(name => Enum.TryParse<AiProviderKind>(name, true, out var kind) ? kind : (AiProviderKind?)null)
            .OfType<AiProviderKind>()
            .ToList();
        var hosts = stored.EndpointHosts ?? [];

        return kinds.Count == 0 && hosts.Length == 0
            ? TenantProviderPolicy.Unrestricted
            : new TenantProviderPolicy(kinds, hosts);
    }

    private sealed record StoredPolicy(string[]? ProviderKinds, string[]? EndpointHosts);
}
