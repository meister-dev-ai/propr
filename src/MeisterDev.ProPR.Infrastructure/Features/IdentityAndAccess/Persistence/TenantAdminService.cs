// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess.Persistence;

/// <summary>EF-backed tenant administration persistence service.</summary>
public sealed class TenantAdminService(
    MeisterProPRDbContext dbContext,
    IHttpContextAccessor? httpContextAccessor = null,
    ILicensingCapabilityService? licensingCapabilityService = null,
    IAiProviderDriverRegistry? providerDrivers = null) : ITenantAdminService
{
    // A composition always supplies the registry. The fallback is for a caller that constructs this without one:
    // no family is loaded, so every stored allow-list entry reads as one nothing claims and the tenant permits
    // nothing, which is the fail-closed side of the same rule.
    private static readonly IAiProviderDriverRegistry NoFamilies = new AiProviderRegistry([]);

    public async Task<IReadOnlyList<TenantDto>> GetAllAsync(CancellationToken ct = default)
    {
        var multiTenancyAvailable = await this.IsMultiTenancyAvailableAsync(ct);

        var tenants = await ApplyTenancyFilter(dbContext.Tenants.AsNoTracking(), multiTenancyAvailable)
            .OrderByDescending(tenant => tenant.CreatedAt)
            .ToListAsync(ct);

        return tenants.Select(this.ToDto).ToList().AsReadOnly();
    }

    public async Task<TenantDto?> GetByIdAsync(Guid tenantId, CancellationToken ct = default)
    {
        var multiTenancyAvailable = await this.IsMultiTenancyAvailableAsync(ct);
        if (!TenantCatalog.IsTenantVisible(tenantId, multiTenancyAvailable))
        {
            return null;
        }

        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .SingleOrDefaultAsync(record => record.Id == tenantId, ct);
        return tenant is null ? null : this.ToDto(tenant);
    }

    // Filtered on the same terms as the lookup by id: a slug and an id name the same tenant, so one must not
    // reach a tenant the other refuses.
    public async Task<TenantDto?> GetBySlugAsync(string tenantSlug, CancellationToken ct = default)
    {
        var multiTenancyAvailable = await this.IsMultiTenancyAvailableAsync(ct);
        var tenant = await ApplyTenancyFilter(dbContext.Tenants, multiTenancyAvailable)
            .FirstOrDefaultAsync(record => record.Slug == tenantSlug, ct);

        return tenant is null ? null : this.ToDto(tenant);
    }

    public async Task<TenantDto> CreateAsync(
        string slug,
        string displayName,
        bool isActive = true,
        bool localLoginEnabled = true,
        CancellationToken ct = default)
    {
        // The refusal names why the capability is unavailable rather than assuming a missing license: it can also
        // be a license that does not cover multi-tenancy, or an override that turned it off.
        if (await this.UnavailableMultiTenancyMessageAsync(ct) is { } refusal)
        {
            throw new InvalidOperationException(refusal);
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

        return this.ToDto(tenant);
    }

    public async Task<TenantDto?> PatchAsync(
        Guid tenantId,
        string? displayName = null,
        bool? isActive = null,
        bool? localLoginEnabled = null,
        IReadOnlyList<string>? allowedAiProviderKinds = null,
        IReadOnlyList<string>? allowedAiEndpointHosts = null,
        IReadOnlyList<string>? removedUnresolvedAiProviderKinds = null,
        CancellationToken ct = default)
    {
        var multiTenancyAvailable = await this.IsMultiTenancyAvailableAsync(ct);
        if (!TenantCatalog.IsTenantVisible(tenantId, multiTenancyAvailable))
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

        if (allowedAiProviderKinds is not null || removedUnresolvedAiProviderKinds is { Count: > 0 })
        {
            // Stored as canonical provider identities, de-duplicated. An empty family list is a meaningful value
            // here: it clears the policy back to unrestricted, which is how a tenant lifts a restriction it no
            // longer wants.
            //
            // An entry no loaded family claims is kept across the write unless it is named for removal. The
            // family list carries only entries a loaded family claims, so replacing the stored list with what a
            // caller stated would drop those entries and lift the restriction they carry — which saving
            // the policy unchanged would otherwise do. Removal is its own input and names the entry, so it is a
            // deliberate act, not a side effect of saving something else.
            var stored = TenantProviderPolicy.FromStored(tenant.AllowedAiProviderKinds, [], providerDrivers ?? NoFamilies);

            // Matched against the stored entry as it is reported, so an operator removes the value shown to them.
            // An entry the tenant does not hold is ignored: the write states the policy that remains, and the
            // returned tenant reports it.
            var removed = (removedUnresolvedAiProviderKinds ?? [])
                .Select(entry => entry.Trim())
                .ToHashSet(StringComparer.Ordinal);

            // A family list the caller left out means the families stay as they are, so they are carried from the
            // stored policy. Removing an entry therefore does not require restating them.
            tenant.AllowedAiProviderKinds =
            [
                .. (allowedAiProviderKinds ?? stored.AllowedKinds)
                .Select(entry => entry.Trim())
                .Where(entry => entry.Length > 0)
                .Distinct(ProviderVocabulary.KeyComparer),
                .. stored.UnresolvedProviderEntries.Where(entry => !removed.Contains(entry)),
            ];
        }

        if (allowedAiEndpointHosts is not null)
        {
            // Stored lower-cased and de-duplicated: a host comparison is case-insensitive, and storing the
            // operator's casing would make the audit trail read as a change when nothing changed.
            tenant.AllowedAiEndpointHosts = allowedAiEndpointHosts
                .Select(host => host.Trim().Trim('/').ToLowerInvariant())
                .Where(host => host.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        tenant.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        await this.AddAuditEntryAsync(
            tenant.Id,
            "tenant.policy.updated",
            $"Updated tenant '{tenant.DisplayName}' policy.",
            $"displayName={tenant.DisplayName}; isActive={tenant.IsActive}; localLoginEnabled={tenant.LocalLoginEnabled}; "
            + $"allowedAiProviderKinds={(tenant.AllowedAiProviderKinds.Length == 0 ? "(unrestricted)" : string.Join(",", tenant.AllowedAiProviderKinds))}; "
            + $"allowedAiEndpointHosts={(tenant.AllowedAiEndpointHosts.Length == 0 ? "(unrestricted)" : string.Join(",", tenant.AllowedAiEndpointHosts))}",
            ct);

        return this.ToDto(tenant);
    }

    public Task<bool> ExistsAsync(Guid tenantId, CancellationToken ct = default)
    {
        return this.ExistsVisibleAsync(tenantId, ct);
    }

    private async Task<bool> ExistsVisibleAsync(Guid tenantId, CancellationToken ct)
    {
        var multiTenancyAvailable = await this.IsMultiTenancyAvailableAsync(ct);
        return await ApplyTenancyFilter(dbContext.Tenants.AsNoTracking(), multiTenancyAvailable)
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

    private TenantDto ToDto(TenantRecord tenant)
    {
        // The stored allow-list is read through the same translation the enforcement path uses, so the operator's
        // view cannot disagree with what is enforced. The entries no loaded family claims are reported alongside
        // the ones that resolved: a tenant whose entries all stopped resolving refuses every provider, and the
        // entry that caused it is the one thing an operator needs to see.
        var policy = TenantProviderPolicy.FromStored(tenant.AllowedAiProviderKinds, tenant.AllowedAiEndpointHosts, providerDrivers ?? NoFamilies);

        return new TenantDto(
            tenant.Id,
            tenant.Slug,
            tenant.DisplayName,
            tenant.IsActive,
            tenant.LocalLoginEnabled,
            TenantCatalog.IsEditable(tenant.Id),
            tenant.CreatedAt,
            tenant.UpdatedAt,
            policy.AllowedKinds,
            tenant.AllowedAiEndpointHosts,
            policy.UnresolvedProviderEntries);
    }

    // Without the licensing module there is no installation state to read, which a deployment with no
    // database configured looks like. Tenancy is left unrestricted there.
    private async ValueTask<bool> IsMultiTenancyAvailableAsync(CancellationToken ct)
    {
        if (licensingCapabilityService is null)
        {
            return true;
        }

        return await licensingCapabilityService.IsEnabledAsync(PremiumCapabilityKey.MultiTenancy, ct);
    }

    /// <summary>
    ///     The message for a refusal, or <see langword="null" /> when multi-tenancy is available. Read from the
    ///     capability so the three ways it can be unavailable are told apart.
    /// </summary>
    private async ValueTask<string?> UnavailableMultiTenancyMessageAsync(CancellationToken ct)
    {
        if (licensingCapabilityService is null)
        {
            return null;
        }

        var capability = await licensingCapabilityService.GetCapabilityAsync(PremiumCapabilityKey.MultiTenancy, ct);

        return capability.IsAvailable
            ? null
            : capability.Message ?? "Multi-tenancy is not available for this installation.";
    }

    private static IQueryable<TenantRecord> ApplyTenancyFilter(IQueryable<TenantRecord> query, bool multiTenancyAvailable)
    {
        return multiTenancyAvailable
            ? query
            : query.Where(tenant => tenant.Id == TenantCatalog.SystemTenantId);
    }
}
