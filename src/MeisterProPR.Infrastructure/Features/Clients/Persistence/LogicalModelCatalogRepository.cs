// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>
///     EF Core / PostgreSQL implementation of <see cref="ILogicalModelCatalogRepository" />. Stores logical models in a
///     tenant-catalog table (<c>ai_logical_models</c>) and a per-client override table (<c>ai_logical_model_overrides</c>),
///     and reads them back by scope.
/// </summary>
public sealed class LogicalModelCatalogRepository(MeisterProPRDbContext db, ILogicalModelCapabilityValidator validator)
    : ILogicalModelCatalogRepository
{
    /// <inheritdoc />
    public async Task AddTenantEntryAsync(Guid tenantId, LogicalModelDto entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // The system tenant (and the unassigned/empty tenant that normalizes to it) has no tenant-catalog layer.
        if (tenantId == Guid.Empty || TenantCatalog.IsSystemTenant(tenantId))
        {
            throw new SystemTenantLogicalModelCatalogException();
        }

        var nameTaken = await db.LogicalModels
            .AnyAsync(x => x.TenantId == tenantId && x.Name == entry.Name, ct);
        if (nameTaken)
        {
            throw new DuplicateLogicalModelException(entry.Name);
        }

        // Config-time capability validation: the mapped model must exist and actually support this role's capability.
        await validator.ValidateAsync(entry, ct);

        var now = DateTimeOffset.UtcNow;
        db.LogicalModels.Add(
            new LogicalModelRecord
            {
                Id = entry.Id == Guid.Empty ? Guid.NewGuid() : entry.Id,
                TenantId = tenantId,
                Name = entry.Name,
                Capability = entry.Capability,
                ConnectionId = entry.ConnectionId,
                ConfiguredModelId = entry.ConfiguredModelId,
                ReasoningEffort = entry.ReasoningEffort,
                ProtocolMode = entry.ProtocolMode,
                CreatedAt = now,
                UpdatedAt = now,
            });
        await this.SaveGuardingDuplicateAsync(entry.Name, ct);
    }

    /// <inheritdoc />
    public async Task AddClientOverrideAsync(Guid clientId, LogicalModelDto entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var nameTaken = await db.LogicalModelOverrides
            .AnyAsync(x => x.ClientId == clientId && x.Name == entry.Name, ct);
        if (nameTaken)
        {
            throw new DuplicateLogicalModelException(entry.Name);
        }

        // Config-time capability validation: the mapped model must exist and actually support this role's capability.
        await validator.ValidateAsync(entry, ct);

        var now = DateTimeOffset.UtcNow;
        db.LogicalModelOverrides.Add(
            new LogicalModelOverrideRecord
            {
                Id = entry.Id == Guid.Empty ? Guid.NewGuid() : entry.Id,
                ClientId = clientId,
                Name = entry.Name,
                Capability = entry.Capability,
                ConnectionId = entry.ConnectionId,
                ConfiguredModelId = entry.ConfiguredModelId,
                ReasoningEffort = entry.ReasoningEffort,
                ProtocolMode = entry.ProtocolMode,
                CreatedAt = now,
                UpdatedAt = now,
            });
        await this.SaveGuardingDuplicateAsync(entry.Name, ct);
    }

    /// <inheritdoc />
    public async Task<bool> UpdateTenantEntryAsync(Guid tenantId, string name, LogicalModelDto entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var record = await db.LogicalModels.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Name == name, ct);
        if (record is null)
        {
            return false;
        }

        await validator.ValidateAsync(entry, ct);
        ApplyMapping(record, entry);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> UpdateClientOverrideAsync(Guid clientId, string name, LogicalModelDto entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var record = await db.LogicalModelOverrides.FirstOrDefaultAsync(x => x.ClientId == clientId && x.Name == name, ct);
        if (record is null)
        {
            return false;
        }

        await validator.ValidateAsync(entry, ct);
        ApplyMapping(record, entry);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // Updates the mapping fields (not the name, which is the key) on either record type.
    private static void ApplyMapping(ILogicalModelMapping record, LogicalModelDto entry)
    {
        switch (record)
        {
            case LogicalModelRecord tenantRecord:
                tenantRecord.Capability = entry.Capability;
                tenantRecord.ConnectionId = entry.ConnectionId;
                tenantRecord.ConfiguredModelId = entry.ConfiguredModelId;
                tenantRecord.ReasoningEffort = entry.ReasoningEffort;
                tenantRecord.ProtocolMode = entry.ProtocolMode;
                tenantRecord.UpdatedAt = DateTimeOffset.UtcNow;
                break;
            case LogicalModelOverrideRecord overrideRecord:
                overrideRecord.Capability = entry.Capability;
                overrideRecord.ConnectionId = entry.ConnectionId;
                overrideRecord.ConfiguredModelId = entry.ConfiguredModelId;
                overrideRecord.ReasoningEffort = entry.ReasoningEffort;
                overrideRecord.ProtocolMode = entry.ProtocolMode;
                overrideRecord.UpdatedAt = DateTimeOffset.UtcNow;
                break;
            default:
                throw new InvalidOperationException($"Unsupported logical-model record type '{record.GetType().Name}'.");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LogicalModelDto>> GetTenantEntriesAsync(Guid tenantId, CancellationToken ct)
    {
        var rows = await db.LogicalModels
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.Name)
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LogicalModelDto>> GetTenantEntriesForClientAsync(Guid clientId, CancellationToken ct)
    {
        var tenantId = await db.Clients
            .AsNoTracking()
            .Where(c => c.Id == clientId)
            .Select(c => (Guid?)c.TenantId)
            .FirstOrDefaultAsync(ct);
        // Unknown client, or a client on the system/empty tenant (which has no tenant-catalog layer) — no tenant
        // entries are visible. Guarding here keeps the "system tenant has no catalog" invariant true on the read side,
        // not merely as a side effect of AddTenantEntryAsync rejecting such rows.
        if (tenantId is null || tenantId.Value == Guid.Empty || TenantCatalog.IsSystemTenant(tenantId.Value))
        {
            return [];
        }

        return await this.GetTenantEntriesAsync(tenantId.Value, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LogicalModelDto>> GetClientOverridesAsync(Guid clientId, CancellationToken ct)
    {
        var rows = await db.LogicalModelOverrides
            .AsNoTracking()
            .Where(x => x.ClientId == clientId)
            .OrderBy(x => x.Name)
            .ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> DeleteTenantEntryAsync(Guid tenantId, string name, CancellationToken ct)
    {
        // A future pass/purpose referrer-check will gate deletion here; no such references exist yet.
        var deleted = await db.LogicalModels
            .Where(x => x.TenantId == tenantId && x.Name == name)
            .ExecuteDeleteAsync(ct);
        return deleted > 0;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteClientOverrideAsync(Guid clientId, string name, CancellationToken ct)
    {
        var deleted = await db.LogicalModelOverrides
            .Where(x => x.ClientId == clientId && x.Name == name)
            .ExecuteDeleteAsync(ct);
        return deleted > 0;
    }

    /// <inheritdoc />
    public async Task<bool> RenameTenantEntryAsync(Guid tenantId, string oldName, string newName, CancellationToken ct)
    {
        var record = await db.LogicalModels.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Name == oldName, ct);
        return await this.RenameAsync(record, oldName, newName, () => db.LogicalModels.AnyAsync(x => x.TenantId == tenantId && x.Name == newName, ct), ct);
    }

    /// <inheritdoc />
    public async Task<bool> RenameClientOverrideAsync(Guid clientId, string oldName, string newName, CancellationToken ct)
    {
        var record = await db.LogicalModelOverrides.FirstOrDefaultAsync(x => x.ClientId == clientId && x.Name == oldName, ct);
        return await this.RenameAsync(
            record, oldName, newName, () => db.LogicalModelOverrides.AnyAsync(x => x.ClientId == clientId && x.Name == newName, ct), ct);
    }

    private async Task<bool> RenameAsync(
        ILogicalModelMapping? record,
        string oldName,
        string newName,
        Func<Task<bool>> newNameTakenAsync,
        CancellationToken ct)
    {
        // Rename only changes the business key; the mapping (connection/model/settings) is unchanged, so no capability
        // re-validation is needed. A future pass/purpose referrer-check will gate rename here.
        if (record is null)
        {
            return false;
        }

        if (string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            return true;
        }

        if (await newNameTakenAsync())
        {
            throw new DuplicateLogicalModelException(newName);
        }

        // The record is a tracked entity (loaded via FirstOrDefaultAsync); both record types expose a settable Name and
        // UpdatedAt, so mutate through the concrete instance.
        switch (record)
        {
            case LogicalModelRecord tenantRecord:
                tenantRecord.Name = newName;
                tenantRecord.UpdatedAt = DateTimeOffset.UtcNow;
                break;
            case LogicalModelOverrideRecord overrideRecord:
                overrideRecord.Name = newName;
                overrideRecord.UpdatedAt = DateTimeOffset.UtcNow;
                break;
            default:
                throw new InvalidOperationException($"Unsupported logical-model record type '{record.GetType().Name}'.");
        }

        // Route through the duplicate backstop so a concurrent claim of newName (between the pre-check and save) still
        // surfaces the friendly DuplicateLogicalModelException rather than a raw unique-violation.
        await this.SaveGuardingDuplicateAsync(newName, ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<string?> GetPurposeRoleAsync(Guid clientId, AiPurpose purpose, CancellationToken ct)
    {
        return await db.ClientPurposeLogicalModels
            .AsNoTracking()
            .Where(x => x.ClientId == clientId && x.Purpose == purpose)
            .Select(x => x.LogicalModelName)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<AiPurpose, string>> GetPurposeRolesAsync(Guid clientId, CancellationToken ct)
    {
        var rows = await db.ClientPurposeLogicalModels
            .AsNoTracking()
            .Where(x => x.ClientId == clientId)
            .ToListAsync(ct);
        return rows.ToDictionary(x => x.Purpose, x => x.LogicalModelName);
    }

    /// <inheritdoc />
    public async Task SetPurposeRoleAsync(Guid clientId, AiPurpose purpose, string logicalModelName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalModelName);

        var existing = await db.ClientPurposeLogicalModels
            .FirstOrDefaultAsync(x => x.ClientId == clientId && x.Purpose == purpose, ct);
        if (existing is null)
        {
            db.ClientPurposeLogicalModels.Add(
                new ClientPurposeLogicalModelRecord
                {
                    Id = Guid.NewGuid(),
                    ClientId = clientId,
                    Purpose = purpose,
                    LogicalModelName = logicalModelName,
                });
        }
        else
        {
            existing.LogicalModelName = logicalModelName;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc />
    public async Task<bool> RemovePurposeRoleAsync(Guid clientId, AiPurpose purpose, CancellationToken ct)
    {
        var deleted = await db.ClientPurposeLogicalModels
            .Where(x => x.ClientId == clientId && x.Purpose == purpose)
            .ExecuteDeleteAsync(ct);
        return deleted > 0;
    }

    private async Task SaveGuardingDuplicateAsync(string name, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: "23505" })
        {
            // Backstop for the read-then-write race between the AnyAsync pre-check and the unique index: a concurrent
            // insert of the same {scope, name} surfaces the friendly exception the contract promises, not a raw
            // DbUpdateException.
            throw new DuplicateLogicalModelException(name);
        }
    }

    private static LogicalModelDto ToDto(ILogicalModelMapping row)
    {
        return new LogicalModelDto(
            row.Id,
            row.Name,
            row.Capability,
            row.ConnectionId,
            row.ConfiguredModelId,
            row.ReasoningEffort,
            row.ProtocolMode);
    }
}
