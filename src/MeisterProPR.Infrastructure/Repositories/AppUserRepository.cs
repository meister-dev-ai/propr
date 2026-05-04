// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IUserRepository" />.</summary>
public sealed class AppUserRepository(MeisterProPRDbContext db) : IUserRepository
{
    public async Task<AppUser?> GetByUsernameAsync(string username, CancellationToken ct = default)
    {
        var record = await db.AppUsers
            .FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower(), ct);
        return record is null ? null : MapToDomain(record);
    }

    public async Task<AppUser?> GetByNormalizedEmailAsync(string normalizedEmail, CancellationToken ct = default)
    {
        var record = await db.AppUsers
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, ct);
        return record is null ? null : MapToDomain(record);
    }

    public async Task<AppUser?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        var record = await db.AppUsers.FindAsync([id], ct);
        return record is null ? null : MapToDomain(record);
    }

    public async Task<AppUser?> GetByIdWithAssignmentsAsync(Guid id, CancellationToken ct = default)
    {
        var record = await db.AppUsers
            .Include(u => u.ClientAssignments)
            .Include(u => u.TenantMemberships)
            .Include(u => u.ExternalIdentities)
            .FirstOrDefaultAsync(u => u.Id == id, ct);
        return record is null ? null : MapToDomain(record);
    }

    public async Task AddAsync(AppUser user, CancellationToken ct = default)
    {
        var record = new AppUserRecord
        {
            Id = user.Id,
            Username = user.Username,
            Email = user.Email,
            NormalizedEmail = user.NormalizedEmail,
            PasswordHash = user.PasswordHash,
            GlobalRole = user.GlobalRole,
            IsActive = user.IsActive,
            CreatedAt = user.CreatedAt,
        };
        db.AppUsers.Add(record);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetActiveAsync(Guid id, bool isActive, CancellationToken ct = default)
    {
        var record = await db.AppUsers.FindAsync([id], ct);
        if (record is null)
        {
            return;
        }

        record.IsActive = isActive;
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdatePasswordHashAsync(Guid id, string passwordHash, CancellationToken ct = default)
    {
        var record = await db.AppUsers.FindAsync([id], ct);
        if (record is null)
        {
            return;
        }

        record.PasswordHash = passwordHash;
        await db.SaveChangesAsync(ct);
    }

    public async Task AddClientAssignmentAsync(UserClientRole assignment, CancellationToken ct = default)
    {
        // Upsert: if assignment already exists for this user+client, update the role.
        var existing = await db.UserClientRoles
            .FirstOrDefaultAsync(r => r.UserId == assignment.UserId && r.ClientId == assignment.ClientId, ct);

        if (existing is not null)
        {
            existing.Role = assignment.Role;
        }
        else
        {
            db.UserClientRoles.Add(
                new UserClientRoleRecord
                {
                    Id = assignment.Id == Guid.Empty ? Guid.NewGuid() : assignment.Id,
                    UserId = assignment.UserId,
                    ClientId = assignment.ClientId,
                    Role = assignment.Role,
                    AssignedAt = assignment.AssignedAt,
                });
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveClientAssignmentAsync(Guid userId, Guid clientId, CancellationToken ct = default)
    {
        var record = await db.UserClientRoles
            .FirstOrDefaultAsync(r => r.UserId == userId && r.ClientId == clientId, ct);
        if (record is not null)
        {
            db.UserClientRoles.Remove(record);
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<IReadOnlyList<AppUser>> ListAsync(CancellationToken ct = default)
    {
        var records = await db.AppUsers
            .Include(u => u.ClientAssignments)
            .Include(u => u.TenantMemberships)
            .Include(u => u.ExternalIdentities)
            .OrderByDescending(u => u.CreatedAt)
            .ToListAsync(ct);
        return records.Select(MapToDomain).ToList().AsReadOnly();
    }

    public async Task<Dictionary<Guid, ClientRole>> GetUserClientRolesAsync(Guid userId, CancellationToken ct = default)
    {
        var records = await db.UserClientRoles
            .Where(r => r.UserId == userId)
            .Select(r => new { r.ClientId, r.Role })
            .ToListAsync(ct);
        return records.ToDictionary(r => r.ClientId, r => r.Role);
    }

    public async Task<AppUser?> GetByExternalIdentityAsync(
        Guid tenantId,
        Guid ssoProviderId,
        string issuer,
        string subject,
        CancellationToken ct = default)
    {
        var userId = await db.ExternalIdentities
            .Where(identity =>
                identity.TenantId == tenantId &&
                identity.SsoProviderId == ssoProviderId &&
                identity.Issuer == issuer &&
                identity.Subject == subject)
            .Select(identity => (Guid?)identity.UserId)
            .FirstOrDefaultAsync(ct);

        return userId.HasValue
            ? await this.GetByIdWithAssignmentsAsync(userId.Value, ct)
            : null;
    }

    public async Task<TenantMembership?> GetTenantMembershipAsync(Guid tenantId, Guid userId, CancellationToken ct = default)
    {
        var record = await db.TenantMemberships
            .FirstOrDefaultAsync(membership => membership.TenantId == tenantId && membership.UserId == userId, ct);

        return record is null ? null : MapMembershipToDomain(record);
    }

    public async Task<IReadOnlyList<TenantMembership>> ListTenantMembershipsAsync(Guid tenantId, CancellationToken ct = default)
    {
        var records = await db.TenantMemberships
            .Where(membership => membership.TenantId == tenantId)
            .OrderBy(membership => membership.AssignedAt)
            .ToListAsync(ct);

        return records.Select(MapMembershipToDomain).ToList().AsReadOnly();
    }

    public async Task<TenantMembership> UpsertTenantMembershipAsync(TenantMembership membership, CancellationToken ct = default)
    {
        var existing = await db.TenantMemberships
            .FirstOrDefaultAsync(
                record => record.TenantId == membership.TenantId && record.UserId == membership.UserId,
                ct);

        if (existing is null)
        {
            existing = new TenantMembershipRecord
            {
                Id = membership.Id == Guid.Empty ? Guid.NewGuid() : membership.Id,
                TenantId = membership.TenantId,
                UserId = membership.UserId,
                Role = membership.Role,
                AssignedAt = membership.AssignedAt,
                UpdatedAt = membership.UpdatedAt,
            };
            db.TenantMemberships.Add(existing);
        }
        else
        {
            existing.Role = membership.Role;
            existing.UpdatedAt = membership.UpdatedAt;
        }

        await db.SaveChangesAsync(ct);
        return MapMembershipToDomain(existing);
    }

    public async Task<TenantMembership?> UpdateTenantMembershipRoleAsync(
        Guid tenantId,
        Guid membershipId,
        TenantRole role,
        CancellationToken ct = default)
    {
        var record = await db.TenantMemberships
            .FirstOrDefaultAsync(membership => membership.TenantId == tenantId && membership.Id == membershipId, ct);

        if (record is null)
        {
            return null;
        }

        record.Role = role;
        record.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return MapMembershipToDomain(record);
    }

    public async Task<bool> RemoveTenantMembershipAsync(Guid tenantId, Guid membershipId, CancellationToken ct = default)
    {
        var record = await db.TenantMemberships
            .FirstOrDefaultAsync(membership => membership.TenantId == tenantId && membership.Id == membershipId, ct);

        if (record is null)
        {
            return false;
        }

        db.TenantMemberships.Remove(record);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task AddExternalIdentityAsync(ExternalIdentity externalIdentity, CancellationToken ct = default)
    {
        db.ExternalIdentities.Add(
            new ExternalIdentityRecord
            {
                Id = externalIdentity.Id == Guid.Empty ? Guid.NewGuid() : externalIdentity.Id,
                TenantId = externalIdentity.TenantId,
                UserId = externalIdentity.UserId,
                SsoProviderId = externalIdentity.SsoProviderId,
                Issuer = externalIdentity.Issuer,
                Subject = externalIdentity.Subject,
                Email = externalIdentity.Email,
                EmailVerified = externalIdentity.EmailVerified,
                CreatedAt = externalIdentity.CreatedAt,
                LastSignInAt = externalIdentity.LastSignInAt,
            });

        await db.SaveChangesAsync(ct);
    }

    private static AppUser MapToDomain(AppUserRecord record)
    {
        var user = new AppUser
        {
            Id = record.Id,
            Username = record.Username,
            Email = record.Email,
            NormalizedEmail = record.NormalizedEmail,
            PasswordHash = record.PasswordHash,
            GlobalRole = record.GlobalRole,
            IsActive = record.IsActive,
            CreatedAt = record.CreatedAt,
        };
        foreach (var a in record.ClientAssignments)
        {
            user.ClientAssignments.Add(
                new UserClientRole
                {
                    Id = a.Id,
                    UserId = a.UserId,
                    ClientId = a.ClientId,
                    Role = a.Role,
                    AssignedAt = a.AssignedAt,
                });
        }

        foreach (var membership in record.TenantMemberships)
        {
            user.TenantMemberships.Add(MapMembershipToDomain(membership));
        }

        foreach (var identity in record.ExternalIdentities)
        {
            user.ExternalIdentities.Add(MapExternalIdentityToDomain(identity));
        }

        return user;
    }

    private static TenantMembership MapMembershipToDomain(TenantMembershipRecord record)
    {
        return new TenantMembership
        {
            Id = record.Id,
            TenantId = record.TenantId,
            UserId = record.UserId,
            Role = record.Role,
            AssignedAt = record.AssignedAt,
            UpdatedAt = record.UpdatedAt,
        };
    }

    private static ExternalIdentity MapExternalIdentityToDomain(ExternalIdentityRecord record)
    {
        return new ExternalIdentity
        {
            Id = record.Id,
            TenantId = record.TenantId,
            UserId = record.UserId,
            SsoProviderId = record.SsoProviderId,
            Issuer = record.Issuer,
            Subject = record.Subject,
            Email = record.Email,
            EmailVerified = record.EmailVerified,
            CreatedAt = record.CreatedAt,
            LastSignInAt = record.LastSignInAt,
        };
    }
}
