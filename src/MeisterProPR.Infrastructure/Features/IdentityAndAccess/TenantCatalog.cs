// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Infrastructure.Features.IdentityAndAccess;

/// <summary>Shared constants and visibility helpers for the internal System tenant.</summary>
public static class TenantCatalog
{
    public const string SystemTenantSlug = "system";
    public const string SystemTenantDisplayName = "System";
    public const bool SystemTenantIsActive = true;
    public const bool SystemTenantLocalLoginEnabled = false;
    public static readonly Guid SystemTenantId = new("11111111-1111-1111-1111-111111111111");

    public static bool IsSystemTenant(Guid tenantId)
    {
        return tenantId == SystemTenantId;
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
