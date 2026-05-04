// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Auth;

/// <summary>Ensures the fixed internal System tenant exists with the expected immutable shape.</summary>
public sealed class SystemTenantBootstrapService(
    MeisterProPRDbContext dbContext,
    ILogger<SystemTenantBootstrapService> logger)
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        var tenant = await dbContext.Tenants
            .SingleOrDefaultAsync(record => record.Id == TenantCatalog.SystemTenantId, ct);

        if (tenant is null)
        {
            tenant = new TenantRecord
            {
                Id = TenantCatalog.SystemTenantId,
                Slug = TenantCatalog.SystemTenantSlug,
                DisplayName = TenantCatalog.SystemTenantDisplayName,
                IsActive = TenantCatalog.SystemTenantIsActive,
                LocalLoginEnabled = TenantCatalog.SystemTenantLocalLoginEnabled,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            dbContext.Tenants.Add(tenant);
            await dbContext.SaveChangesAsync(ct);
            logger.LogInformation(
                "System tenant bootstrap: created internal tenant '{TenantSlug}' (id={TenantId}).",
                tenant.Slug,
                tenant.Id);
            return;
        }

        var changed = false;

        if (!string.Equals(tenant.Slug, TenantCatalog.SystemTenantSlug, StringComparison.Ordinal))
        {
            tenant.Slug = TenantCatalog.SystemTenantSlug;
            changed = true;
        }

        if (!string.Equals(tenant.DisplayName, TenantCatalog.SystemTenantDisplayName, StringComparison.Ordinal))
        {
            tenant.DisplayName = TenantCatalog.SystemTenantDisplayName;
            changed = true;
        }

        if (!tenant.IsActive)
        {
            tenant.IsActive = true;
            changed = true;
        }

        if (tenant.LocalLoginEnabled != TenantCatalog.SystemTenantLocalLoginEnabled)
        {
            tenant.LocalLoginEnabled = TenantCatalog.SystemTenantLocalLoginEnabled;
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        tenant.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        logger.LogInformation(
            "System tenant bootstrap: normalized internal tenant '{TenantSlug}' (id={TenantId}).",
            tenant.Slug,
            tenant.Id);
    }
}
