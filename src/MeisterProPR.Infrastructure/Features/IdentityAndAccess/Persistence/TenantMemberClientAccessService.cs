// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Features.IdentityAndAccess.Persistence;

/// <summary>EF-backed tenant member client-access management, scoped and validated to a single tenant.</summary>
public sealed class TenantMemberClientAccessService(
    MeisterProPRDbContext dbContext,
    IHttpContextAccessor? httpContextAccessor = null) : ITenantMemberClientAccessService
{
    public async Task<IReadOnlyList<TenantClientSummaryDto>> ListTenantClientsAsync(
        Guid tenantId,
        CancellationToken ct = default)
    {
        return await dbContext.Clients
            .AsNoTracking()
            .Where(client => client.TenantId == tenantId)
            .OrderBy(client => client.DisplayName)
            .Select(client => new TenantClientSummaryDto(client.Id, client.DisplayName, client.IsActive))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<TenantMemberClientAccessDto>?> ListMemberAccessAsync(
        Guid tenantId,
        Guid membershipId,
        CancellationToken ct = default)
    {
        var userId = await this.ResolveMemberUserIdAsync(tenantId, membershipId, ct);
        if (userId is null)
        {
            return null;
        }

        // Join through clients so stale assignments to clients outside this tenant never surface here.
        // Order by the mapped client column before projecting — ordering by a projected DTO member is
        // not translatable by the relational provider.
        return await dbContext.UserClientRoles
            .AsNoTracking()
            .Where(role => role.UserId == userId.Value)
            .Join(
                dbContext.Clients.Where(client => client.TenantId == tenantId),
                role => role.ClientId,
                client => client.Id,
                (role, client) => new { role, client })
            .OrderBy(pair => pair.client.DisplayName)
            .Select(pair => new TenantMemberClientAccessDto(
                pair.client.Id,
                pair.client.DisplayName,
                pair.role.Role,
                pair.role.AssignedAt))
            .ToListAsync(ct);
    }

    public async Task<TenantMemberClientAccessResult> AssignAsync(
        Guid tenantId,
        Guid membershipId,
        Guid clientId,
        ClientRole role,
        CancellationToken ct = default)
    {
        EnsureTenantIsEditable(tenantId);

        var userId = await this.ResolveMemberUserIdAsync(tenantId, membershipId, ct);
        if (userId is null)
        {
            return new TenantMemberClientAccessResult(TenantMemberClientAccessOutcome.MembershipNotFound, null);
        }

        var client = await dbContext.Clients
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == clientId && candidate.TenantId == tenantId, ct);
        if (client is null)
        {
            return new TenantMemberClientAccessResult(TenantMemberClientAccessOutcome.ClientNotInTenant, null);
        }

        var existing = await dbContext.UserClientRoles
            .FirstOrDefaultAsync(assignment => assignment.UserId == userId.Value && assignment.ClientId == clientId, ct);

        DateTimeOffset assignedAt;
        if (existing is null)
        {
            assignedAt = DateTimeOffset.UtcNow;
            dbContext.UserClientRoles.Add(
                new UserClientRoleRecord
                {
                    Id = Guid.NewGuid(),
                    UserId = userId.Value,
                    ClientId = clientId,
                    Role = role,
                    AssignedAt = assignedAt,
                });
        }
        else
        {
            existing.Role = role;
            assignedAt = existing.AssignedAt;
        }

        await dbContext.SaveChangesAsync(ct);
        await this.AddAuditEntryAsync(
            tenantId,
            "tenant.client-access.assigned",
            $"Granted {role} on client {client.DisplayName} to user {userId.Value}.",
            $"membershipId={membershipId}; userId={userId.Value}; clientId={clientId}; role={role}",
            ct);

        return new TenantMemberClientAccessResult(
            TenantMemberClientAccessOutcome.Success,
            new TenantMemberClientAccessDto(clientId, client.DisplayName, role, assignedAt));
    }

    public async Task<TenantMemberClientAccessOutcome> RemoveAsync(
        Guid tenantId,
        Guid membershipId,
        Guid clientId,
        CancellationToken ct = default)
    {
        EnsureTenantIsEditable(tenantId);

        var userId = await this.ResolveMemberUserIdAsync(tenantId, membershipId, ct);
        if (userId is null)
        {
            return TenantMemberClientAccessOutcome.MembershipNotFound;
        }

        // Only revoke access to clients that belong to this tenant, so a tenant administrator can never
        // touch a member's assignments to clients in other tenants the member may also belong to.
        var clientInTenant = await dbContext.Clients
            .AnyAsync(client => client.Id == clientId && client.TenantId == tenantId, ct);
        if (!clientInTenant)
        {
            return TenantMemberClientAccessOutcome.Success;
        }

        var existing = await dbContext.UserClientRoles
            .FirstOrDefaultAsync(assignment => assignment.UserId == userId.Value && assignment.ClientId == clientId, ct);
        if (existing is not null)
        {
            dbContext.UserClientRoles.Remove(existing);
            await dbContext.SaveChangesAsync(ct);
            await this.AddAuditEntryAsync(
                tenantId,
                "tenant.client-access.removed",
                $"Revoked client {clientId} access for user {userId.Value}.",
                $"membershipId={membershipId}; userId={userId.Value}; clientId={clientId}",
                ct);
        }

        return TenantMemberClientAccessOutcome.Success;
    }

    private async Task<Guid?> ResolveMemberUserIdAsync(Guid tenantId, Guid membershipId, CancellationToken ct)
    {
        return await dbContext.TenantMemberships
            .AsNoTracking()
            .Where(membership => membership.TenantId == tenantId && membership.Id == membershipId)
            .Select(membership => (Guid?)membership.UserId)
            .FirstOrDefaultAsync(ct);
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

    private static void EnsureTenantIsEditable(Guid tenantId)
    {
        if (!TenantCatalog.IsEditable(tenantId))
        {
            throw new InvalidOperationException("The internal System tenant cannot be modified.");
        }
    }
}
