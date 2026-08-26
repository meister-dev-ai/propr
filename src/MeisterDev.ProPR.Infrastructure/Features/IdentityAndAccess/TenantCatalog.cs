// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain;
using MeisterDev.ProPR.Infrastructure.Data.Models;

namespace MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;

/// <summary>Shared constants and visibility helpers for the internal System tenant.</summary>
public static class TenantCatalog
{
    public const string SystemTenantSlug = "system";
    public const string SystemTenantDisplayName = "System";
    public const bool SystemTenantIsActive = true;
    public const bool SystemTenantLocalLoginEnabled = false;

    /// <summary>
    ///     Taken from the domain rather than restated here. Rules above this layer depend on it, such as
    ///     a runner enrolled in this tenant serving every other tenant. A second copy of the identifier
    ///     could differ from the first.
    /// </summary>
    public static readonly Guid SystemTenantId = SystemTenant.Id;

    public static bool IsSystemTenant(Guid tenantId)
    {
        return SystemTenant.Is(tenantId);
    }

    public static bool IsEditable(Guid tenantId)
    {
        return !IsSystemTenant(tenantId);
    }

    /// <summary>
    ///     Whether a client belonging to <paramref name="tenantId" /> is in scope for the installation.
    ///     <para>
    ///         Without multi-tenancy only the System tenant is in scope, together with clients that carry no
    ///         tenant at all: those predate tenancy and stay reachable so an upgrade does not hide them.
    ///     </para>
    /// </summary>
    /// <param name="tenantId">The tenant the client belongs to.</param>
    /// <param name="multiTenancyAvailable">Whether the multi-tenancy capability is available.</param>
    /// <returns>Whether the client is visible.</returns>
    public static bool IsClientVisible(Guid tenantId, bool multiTenancyAvailable)
    {
        return multiTenancyAvailable || tenantId == Guid.Empty || IsSystemTenant(tenantId);
    }

    /// <summary>
    ///     Narrows a client query to the clients in scope for the installation.
    /// </summary>
    /// <remarks>
    ///     The same rule as <see cref="IsClientVisible" />, expressed as a query so a read that has to decide
    ///     over many rows does not load them first. Every surface that lists, counts or resolves clients
    ///     narrows through here, so an installation cannot report holding a client it cannot show.
    /// </remarks>
    /// <param name="clients">The clients to narrow.</param>
    /// <param name="multiTenancyAvailable">Whether the multi-tenancy capability is available.</param>
    /// <returns>The clients in scope.</returns>
    public static IQueryable<ClientRecord> VisibleClients(
        IQueryable<ClientRecord> clients,
        bool multiTenancyAvailable)
    {
        ArgumentNullException.ThrowIfNull(clients);

        return multiTenancyAvailable
            ? clients
            : clients.Where(client => client.TenantId == Guid.Empty || client.TenantId == SystemTenantId);
    }

    /// <summary>
    ///     Whether the tenant is in scope for the installation. Without multi-tenancy only the System tenant is.
    /// </summary>
    /// <param name="tenantId">The tenant to check.</param>
    /// <param name="multiTenancyAvailable">Whether the multi-tenancy capability is available.</param>
    /// <returns>Whether the tenant is visible.</returns>
    public static bool IsTenantVisible(Guid tenantId, bool multiTenancyAvailable)
    {
        return multiTenancyAvailable || IsSystemTenant(tenantId);
    }
}
