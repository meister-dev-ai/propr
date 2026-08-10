// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain;

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

    public static bool IsClientVisible(Guid tenantId, bool isCommunityEdition)
    {
        return !isCommunityEdition || tenantId == Guid.Empty || IsSystemTenant(tenantId);
    }

    public static bool IsTenantVisible(Guid tenantId, bool isCommunityEdition)
    {
        return !isCommunityEdition || IsSystemTenant(tenantId);
    }
}
