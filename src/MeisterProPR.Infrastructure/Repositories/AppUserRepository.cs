using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IUserRepository"/>.</summary>
public sealed class AppUserRepository(MeisterProPRDbContext db) : IUserRepository
{
    public async Task<AppUser?> GetByUsernameAsync(string username, CancellationToken ct = default)
    {
        var record = await db.AppUsers
            .FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower(), ct);
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
            .FirstOrDefaultAsync(u => u.Id == id, ct);
        return record is null ? null : MapToDomain(record);
    }

    public async Task AddAsync(AppUser user, CancellationToken ct = default)
    {
        var record = new AppUserRecord
        {
            Id = user.Id,
            Username = user.Username,
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
            db.UserClientRoles.Add(new UserClientRoleRecord
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
            .OrderByDescending(u => u.CreatedAt)
            .ToListAsync(ct);
        return records.Select(MapToDomain).ToList().AsReadOnly();
    }

    public async Task<ClientRole?> GetClientRoleAsync(Guid userId, Guid clientId, CancellationToken ct = default)
    {
        var record = await db.UserClientRoles
            .FirstOrDefaultAsync(r => r.UserId == userId && r.ClientId == clientId, ct);
        return record?.Role;
    }

    private static AppUser MapToDomain(AppUserRecord record)
    {
        var user = new AppUser
        {
            Id = record.Id,
            Username = record.Username,
            PasswordHash = record.PasswordHash,
            GlobalRole = record.GlobalRole,
            IsActive = record.IsActive,
            CreatedAt = record.CreatedAt,
        };
        foreach (var a in record.ClientAssignments)
        {
            user.ClientAssignments.Add(new UserClientRole
            {
                Id = a.Id,
                UserId = a.UserId,
                ClientId = a.ClientId,
                Role = a.Role,
                AssignedAt = a.AssignedAt,
            });
        }

        return user;
    }
}
