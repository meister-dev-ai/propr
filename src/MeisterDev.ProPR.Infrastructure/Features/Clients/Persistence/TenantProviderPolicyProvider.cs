// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Repositories;

/// <summary>
///     Reads the provider-kind allow-list off the tenant row.
/// </summary>
/// <remarks>
///     The system tenant has no allow-list surface, so it is answered as unrestricted without a query: a policy
///     nobody can edit could only ever be a trap. A stored identity this build cannot name is kept as an opaque
///     key that matches no family, so a tenant whose entries stopped resolving permits nothing and every
///     connection it owns is refused. Each such entry is logged on every read, because a tenant that refuses
///     everything is only actionable once an operator can see which entry was not understood.
/// </remarks>
public sealed partial class TenantProviderPolicyProvider(
    IDbContextFactory<MeisterProPRDbContext> contextFactory,
    IAiProviderDriverRegistry providerDrivers,
    ILogger<TenantProviderPolicyProvider> logger)
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

        return this.ToPolicy(tenantId, stored);
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

        return this.ToPolicy(tenantId, stored);
    }

    private TenantProviderPolicy ToPolicy(Guid tenantId, StoredPolicy? stored)
    {
        if (stored is null)
        {
            return TenantProviderPolicy.Unrestricted;
        }

        var policy = TenantProviderPolicy.FromStored(stored.ProviderKinds, stored.EndpointHosts, providerDrivers);

        // Logged per read, not once per process: the policy is read on every enforcement and is not cached, so a
        // record only of the first read would age out of a log window while the tenant is still refusing
        // everything.
        foreach (var entry in policy.UnresolvedProviderEntries)
        {
            LogUnresolvedAllowListEntry(logger, tenantId, entry);
        }

        return policy;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "Tenant {TenantId} has permitted-provider entry '{ProviderIdentity}', which no loaded provider family claims. The entry permits nothing, and the tenant stays restricted until it is corrected.")]
    private static partial void LogUnresolvedAllowListEntry(ILogger logger, Guid tenantId, string providerIdentity);

    private sealed record StoredPolicy(string[]? ProviderKinds, string[]? EndpointHosts);
}
