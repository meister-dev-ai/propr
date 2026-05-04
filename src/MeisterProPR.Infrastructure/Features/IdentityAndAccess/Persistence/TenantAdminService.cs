// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Features.IdentityAndAccess.Persistence;

/// <summary>EF-backed tenant administration persistence service.</summary>
public sealed class TenantAdminService(
    MeisterProPRDbContext dbContext,
    IHttpContextAccessor? httpContextAccessor = null,
    ILicensingCapabilityService? licensingCapabilityService = null) : ITenantAdminService
{
    public async Task<IReadOnlyList<TenantDto>> GetAllAsync(CancellationToken ct = default)
    {
        var isCommunityEdition = await this.IsCommunityEditionAsync(ct);

        var tenants = await ApplyEditionFilter(dbContext.Tenants.AsNoTracking(), isCommunityEdition)
            .OrderByDescending(tenant => tenant.CreatedAt)
            .ToListAsync(ct);

        return tenants.Select(ToDto).ToList().AsReadOnly();
    }

    public async Task<TenantDto?> GetByIdAsync(Guid tenantId, CancellationToken ct = default)
    {
        var isCommunityEdition = await this.IsCommunityEditionAsync(ct);
        if (!TenantCatalog.IsTenantVisible(tenantId, isCommunityEdition))
        {
            return null;
        }

        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == tenantId, ct);
        return tenant is null ? null : ToDto(tenant);
    }

    public async Task<TenantDto?> GetBySlugAsync(string tenantSlug, CancellationToken ct = default)
    {
        var tenant = await dbContext.Tenants
            .FirstOrDefaultAsync(record => record.Slug == tenantSlug, ct);

        return tenant is null ? null : ToDto(tenant);
    }

    public async Task<TenantDto> CreateAsync(
        string slug,
        string displayName,
        bool isActive = true,
        bool localLoginEnabled = true,
        CancellationToken ct = default)
    {
        if (await this.IsCommunityEditionAsync(ct))
        {
            throw new InvalidOperationException("Community edition only supports the internal System tenant.");
        }

        var now = DateTimeOffset.UtcNow;
        var tenant = new TenantRecord
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            DisplayName = displayName,
            IsActive = isActive,
            LocalLoginEnabled = localLoginEnabled,
            CreatedAt = now,
            UpdatedAt = now,
        };

        dbContext.Tenants.Add(tenant);
        await dbContext.SaveChangesAsync(ct);
        await this.AddAuditEntryAsync(
            tenant.Id,
            "tenant.created",
            $"Created tenant '{tenant.DisplayName}' ({tenant.Slug}).",
            null,
            ct);

        return ToDto(tenant);
    }

    public async Task<TenantDto?> PatchAsync(
        Guid tenantId,
        string? displayName = null,
        bool? isActive = null,
        bool? localLoginEnabled = null,
        CancellationToken ct = default)
    {
        var isCommunityEdition = await this.IsCommunityEditionAsync(ct);
        if (!TenantCatalog.IsTenantVisible(tenantId, isCommunityEdition))
        {
            return null;
        }

        var tenant = await dbContext.Tenants.FindAsync([tenantId], ct);
        if (tenant is null)
        {
            return null;
        }

        if (!TenantCatalog.IsEditable(tenant.Id))
        {
            throw new InvalidOperationException("The internal System tenant cannot be modified.");
        }

        if (displayName is not null)
        {
            tenant.DisplayName = displayName;
        }

        if (isActive.HasValue)
        {
            tenant.IsActive = isActive.Value;
        }

        if (localLoginEnabled.HasValue)
        {
            tenant.LocalLoginEnabled = localLoginEnabled.Value;
        }

        tenant.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        await this.AddAuditEntryAsync(
            tenant.Id,
            "tenant.policy.updated",
            $"Updated tenant '{tenant.DisplayName}' policy.",
            $"displayName={tenant.DisplayName}; isActive={tenant.IsActive}; localLoginEnabled={tenant.LocalLoginEnabled}",
            ct);

        return ToDto(tenant);
    }

    public Task<bool> ExistsAsync(Guid tenantId, CancellationToken ct = default)
    {
        return this.ExistsVisibleAsync(tenantId, ct);
    }

    private async Task<bool> ExistsVisibleAsync(Guid tenantId, CancellationToken ct)
    {
        var isCommunityEdition = await this.IsCommunityEditionAsync(ct);
        return await ApplyEditionFilter(dbContext.Tenants.AsNoTracking(), isCommunityEdition)
            .AnyAsync(tenant => tenant.Id == tenantId, ct);
    }

    private async Task AddAuditEntryAsync(
        Guid tenantId,
        string eventType,
        string summary,
        string? detail,
        CancellationToken ct)
    {
        dbContext.TenantAuditEntries.Add(
            new TenantAuditEntryRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ActorUserId = ResolveActorUserId(httpContextAccessor),
                EventType = eventType,
                Summary = summary,
                Detail = detail,
                OccurredAt = DateTimeOffset.UtcNow,
            });

        await dbContext.SaveChangesAsync(ct);
    }

    private static Guid? ResolveActorUserId(IHttpContextAccessor? httpContextAccessor)
    {
        var rawUserId = httpContextAccessor?.HttpContext?.Items["UserId"] as string;
        return Guid.TryParse(rawUserId, out var actorUserId) ? actorUserId : null;
    }

    private static TenantDto ToDto(TenantRecord tenant)
    {
        return new TenantDto(
            tenant.Id,
            tenant.Slug,
            tenant.DisplayName,
            tenant.IsActive,
            tenant.LocalLoginEnabled,
            TenantCatalog.IsEditable(tenant.Id),
            tenant.CreatedAt,
            tenant.UpdatedAt);
    }

    private async Task<bool> IsCommunityEditionAsync(CancellationToken ct)
    {
        if (licensingCapabilityService is null)
        {
            return false;
        }

        var summaryTask = licensingCapabilityService.GetSummaryAsync(ct);
        if (summaryTask is null)
        {
            return false;
        }

        var summary = await summaryTask;
        return summary?.Edition == InstallationEdition.Community;
    }

    private static IQueryable<TenantRecord> ApplyEditionFilter(IQueryable<TenantRecord> query, bool isCommunityEdition)
    {
        return isCommunityEdition
            ? query.Where(tenant => tenant.Id == TenantCatalog.SystemTenantId)
            : query;
    }
}
