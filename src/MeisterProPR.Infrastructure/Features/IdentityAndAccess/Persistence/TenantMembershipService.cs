// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Features.IdentityAndAccess.Persistence;

/// <summary>EF-backed tenant membership persistence service.</summary>
public sealed class TenantMembershipService(
    MeisterProPRDbContext dbContext,
    IHttpContextAccessor? httpContextAccessor = null) : ITenantMembershipService
{
    public async Task<IReadOnlyList<TenantMembershipDto>> ListAsync(Guid tenantId, CancellationToken ct = default)
    {
        var memberships = await dbContext.TenantMemberships
            .AsNoTracking()
            .Include(membership => membership.User)
            .Where(membership => membership.TenantId == tenantId)
            .OrderBy(membership => membership.AssignedAt)
            .ToListAsync(ct);

        return memberships.Select(ToDto).ToList().AsReadOnly();
    }

    public async Task<TenantMembershipDto?> GetByIdAsync(Guid tenantId, Guid membershipId, CancellationToken ct = default)
    {
        var membership = await dbContext.TenantMemberships
            .AsNoTracking()
            .Include(record => record.User)
            .FirstOrDefaultAsync(record => record.TenantId == tenantId && record.Id == membershipId, ct);

        return membership is null ? null : ToDto(membership);
    }

    public async Task<TenantMembershipDto?> GetByUserAsync(Guid tenantId, Guid userId, CancellationToken ct = default)
    {
        var membership = await dbContext.TenantMemberships
            .AsNoTracking()
            .Include(record => record.User)
            .FirstOrDefaultAsync(record => record.TenantId == tenantId && record.UserId == userId, ct);

        return membership is null ? null : ToDto(membership);
    }

    public async Task<TenantMembershipDto> UpsertAsync(
        Guid tenantId,
        Guid userId,
        TenantRole role,
        CancellationToken ct = default)
    {
        EnsureTenantIsEditable(tenantId);

        var membership = await dbContext.TenantMemberships
            .Include(record => record.User)
            .FirstOrDefaultAsync(record => record.TenantId == tenantId && record.UserId == userId, ct);

        if (membership is null)
        {
            membership = new TenantMembershipRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = userId,
                Role = role,
                AssignedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            dbContext.TenantMemberships.Add(membership);
            await dbContext.SaveChangesAsync(ct);
            membership = await dbContext.TenantMemberships
                .Include(record => record.User)
                .SingleAsync(record => record.Id == membership.Id, ct);
            await this.AddAuditEntryAsync(
                tenantId,
                "tenant.membership.assigned",
                $"Assigned tenant role {membership.Role} to {membership.User?.Username ?? membership.UserId.ToString()}.",
                $"membershipId={membership.Id}; userId={membership.UserId}; role={membership.Role}",
                ct);
        }
        else
        {
            await this.EnsureTenantRetainsAdministratorAsync(tenantId, membership, role, ct);
            membership.Role = role;
            membership.UpdatedAt = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(ct);
            await this.AddAuditEntryAsync(
                tenantId,
                "tenant.membership.updated",
                $"Updated tenant role for {membership.User?.Username ?? membership.UserId.ToString()} to {membership.Role}.",
                $"membershipId={membership.Id}; userId={membership.UserId}; role={membership.Role}",
                ct);
        }

        return ToDto(membership);
    }

    public async Task<TenantMembershipDto?> PatchAsync(
        Guid tenantId,
        Guid membershipId,
        TenantRole role,
        CancellationToken ct = default)
    {
        EnsureTenantIsEditable(tenantId);

        var membership = await dbContext.TenantMemberships
            .Include(record => record.User)
            .FirstOrDefaultAsync(record => record.TenantId == tenantId && record.Id == membershipId, ct);

        if (membership is null)
        {
            return null;
        }

        await this.EnsureTenantRetainsAdministratorAsync(tenantId, membership, role, ct);
        membership.Role = role;
        membership.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(ct);
        await this.AddAuditEntryAsync(
            tenantId,
            "tenant.membership.updated",
            $"Updated tenant role for {membership.User?.Username ?? membership.UserId.ToString()} to {membership.Role}.",
            $"membershipId={membership.Id}; userId={membership.UserId}; role={membership.Role}",
            ct);

        return ToDto(membership);
    }

    public async Task<bool> DeleteAsync(Guid tenantId, Guid membershipId, CancellationToken ct = default)
    {
        EnsureTenantIsEditable(tenantId);

        var membership = await dbContext.TenantMemberships
            .FirstOrDefaultAsync(record => record.TenantId == tenantId && record.Id == membershipId, ct);

        if (membership is null)
        {
            return false;
        }

        await this.EnsureTenantRetainsAdministratorAsync(tenantId, membership, null, ct);
        dbContext.TenantMemberships.Remove(membership);
        await dbContext.SaveChangesAsync(ct);
        await this.AddAuditEntryAsync(
            tenantId,
            "tenant.membership.removed",
            $"Removed tenant membership for {membership.UserId}.",
            $"membershipId={membership.Id}; userId={membership.UserId}",
            ct);
        return true;
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

    private async Task EnsureTenantRetainsAdministratorAsync(
        Guid tenantId,
        TenantMembershipRecord membership,
        TenantRole? replacementRole,
        CancellationToken ct)
    {
        var remainsAdministrator = replacementRole is TenantRole.TenantAdministrator;
        if (membership.Role != TenantRole.TenantAdministrator || remainsAdministrator)
        {
            return;
        }

        var otherAdministrators = await dbContext.TenantMemberships
            .CountAsync(
                record => record.TenantId == tenantId
                          && record.Role == TenantRole.TenantAdministrator
                          && record.Id != membership.Id,
                ct);

        if (otherAdministrators == 0)
        {
            throw new InvalidOperationException("Tenant must retain at least one tenant administrator.");
        }
    }

    private static Guid? ResolveActorUserId(IHttpContextAccessor? httpContextAccessor)
    {
        var rawUserId = httpContextAccessor?.HttpContext?.Items["UserId"] as string;
        return Guid.TryParse(rawUserId, out var actorUserId) ? actorUserId : null;
    }

    private static void EnsureTenantIsEditable(Guid tenantId)
    {
        if (!TenantCatalog.IsEditable(tenantId))
        {
            throw new InvalidOperationException("The internal System tenant cannot be modified.");
        }
    }

    private static TenantMembershipDto ToDto(TenantMembershipRecord membership)
    {
        return new TenantMembershipDto(
            membership.Id,
            membership.TenantId,
            membership.UserId,
            membership.User?.Username ?? string.Empty,
            membership.User?.Email,
            membership.User?.IsActive ?? false,
            membership.Role,
            membership.AssignedAt,
            membership.UpdatedAt);
    }
}
