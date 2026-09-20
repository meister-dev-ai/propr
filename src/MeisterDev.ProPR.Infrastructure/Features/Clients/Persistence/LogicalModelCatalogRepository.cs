// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MeisterDev.ProPR.Infrastructure.Repositories;

/// <summary>
///     EF Core / PostgreSQL implementation of <see cref="ILogicalModelCatalogRepository" />. Stores logical models in a
///     tenant-catalog table (<c>ai_logical_models</c>) and a per-client override table (<c>ai_logical_model_overrides</c>),
///     and reads them back by scope.
/// </summary>
/// <remarks>
///     The purpose-role reads take their own short-lived context when a factory is available. They are called from
///     the per-file review loop, which reviews several files at once, and a scoped context serves one operation at a
///     time — concurrent readers on the shared instance make Entity Framework refuse the second one. The write paths
///     keep the scoped context: they run from a single request and rely on its change tracking.
/// </remarks>
public sealed class LogicalModelCatalogRepository(
    MeisterProPRDbContext db,
    ILogicalModelCapabilityValidator validator,
    IAiConnectionRepository connections,
    IAiConnectionScopeGuard scopeGuard,
    IAiProviderDriverRegistry providerDrivers,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null)
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
        await this.EnsureConnectionIsInTenantScopeAsync(tenantId, entry, ct);

        var family = await this.FamilyOfAsync(entry.ConnectionId, ct);
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
                ProtocolMode = this.ProtocolModeToStore(null, family, entry.ProtocolMode),
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
        await this.EnsureConnectionIsInClientTenantScopeAsync(clientId, entry, ct);

        var family = await this.FamilyOfAsync(entry.ConnectionId, ct);
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
                ProtocolMode = this.ProtocolModeToStore(null, family, entry.ProtocolMode),
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
        await this.EnsureConnectionIsInTenantScopeAsync(tenantId, entry, ct);
        this.ApplyMapping(record, entry, await this.FamilyOfAsync(entry.ConnectionId, ct));
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
        await this.EnsureConnectionIsInClientTenantScopeAsync(clientId, entry, ct);
        this.ApplyMapping(record, entry, await this.FamilyOfAsync(entry.ConnectionId, ct));
        await db.SaveChangesAsync(ct);
        return true;
    }

    // A mapping stores an explicit connection id, and that connection is later read back by id without being
    // re-scoped to the requesting client. The tenant boundary is therefore enforced on the write path as well, so a
    // reference to another tenant's connection is rejected before it can be persisted rather than only at run time.
    private async Task EnsureConnectionIsInTenantScopeAsync(Guid tenantId, LogicalModelDto entry, CancellationToken ct)
    {
        var connection = await connections.GetByIdAsync(entry.ConnectionId, ct);
        if (connection is null)
        {
            // A missing connection is the capability validator's concern; it runs first and reports it.
            return;
        }

        var refusal = await scopeGuard.ValidateAsync(connection, tenantId, ct);
        if (refusal is not null)
        {
            throw new LogicalModelReferenceInvalidException(entry.Name, refusal);
        }
    }

    private async Task EnsureConnectionIsInClientTenantScopeAsync(Guid clientId, LogicalModelDto entry, CancellationToken ct)
    {
        var tenantId = await db.Clients
            .Where(c => c.Id == clientId)
            .Select(c => (Guid?)c.TenantId)
            .FirstOrDefaultAsync(ct);

        if (tenantId is not { } resolved || resolved == Guid.Empty)
        {
            throw new LogicalModelReferenceInvalidException(
                entry.Name,
                $"client '{clientId}' has no resolvable tenant, so a connection reference cannot be validated.");
        }

        await this.EnsureConnectionIsInTenantScopeAsync(resolved, entry, ct);
    }

    // Updates the mapping fields (not the name, which is the key) on either record type. The family is the one
    // the entry now points at, so repointing a row to a connection of another family rewrites the protocol mode
    // under that family's names.
    private void ApplyMapping(ILogicalModelMapping record, LogicalModelDto entry, string? family)
    {
        var protocolMode = this.ProtocolModeToStore(record.ProtocolMode, family, entry.ProtocolMode);

        switch (record)
        {
            case LogicalModelRecord tenantRecord:
                tenantRecord.Capability = entry.Capability;
                tenantRecord.ConnectionId = entry.ConnectionId;
                tenantRecord.ConfiguredModelId = entry.ConfiguredModelId;
                tenantRecord.ReasoningEffort = entry.ReasoningEffort;
                tenantRecord.ProtocolMode = protocolMode;
                tenantRecord.UpdatedAt = DateTimeOffset.UtcNow;
                break;
            case LogicalModelOverrideRecord overrideRecord:
                overrideRecord.Capability = entry.Capability;
                overrideRecord.ConnectionId = entry.ConnectionId;
                overrideRecord.ConfiguredModelId = entry.ConfiguredModelId;
                overrideRecord.ReasoningEffort = entry.ReasoningEffort;
                overrideRecord.ProtocolMode = protocolMode;
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
        return await this.ToDtosAsync(rows, ct);
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
        return await this.ToDtosAsync(rows, ct);
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
        if (contextFactory is null)
        {
            return await ReadPurposeRoleAsync(db, clientId, purpose, ct);
        }

        await using var isolated = await contextFactory.CreateDbContextAsync(ct);
        return await ReadPurposeRoleAsync(isolated, clientId, purpose, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<AiPurpose, string>> GetPurposeRolesAsync(Guid clientId, CancellationToken ct)
    {
        if (contextFactory is null)
        {
            return await ReadPurposeRolesAsync(db, clientId, ct);
        }

        await using var isolated = await contextFactory.CreateDbContextAsync(ct);
        return await ReadPurposeRolesAsync(isolated, clientId, ct);
    }

    private static async Task<string?> ReadPurposeRoleAsync(
        MeisterProPRDbContext context,
        Guid clientId,
        AiPurpose purpose,
        CancellationToken ct)
    {
        return await context.ClientPurposeLogicalModels
            .AsNoTracking()
            .Where(x => x.ClientId == clientId && x.Purpose == purpose)
            .Select(x => x.LogicalModelName)
            .FirstOrDefaultAsync(ct);
    }

    private static async Task<IReadOnlyDictionary<AiPurpose, string>> ReadPurposeRolesAsync(
        MeisterProPRDbContext context,
        Guid clientId,
        CancellationToken ct)
    {
        var rows = await context.ClientPurposeLogicalModels
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

    private async Task<IReadOnlyList<LogicalModelDto>> ToDtosAsync(
        IReadOnlyList<ILogicalModelMapping> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var families = await this.FamiliesOfAsync(rows.Select(row => row.ConnectionId), ct);
        return rows
            .Select(row => this.ToDto(row, families.TryGetValue(row.ConnectionId, out var family) ? family : null))
            .ToList();
    }

    // The stored protocol mode is a name, and a name no loaded family claims is reported on the entry rather
    // than thrown: the read is a list projection, and a failure inside one takes out every other entry being
    // projected with it. It is read against the family of the connection the row maps to, because a protocol mode
    // a family declares persists qualified by that family's key. Where no family can be determined only the
    // host-reserved shapes resolve, which a row carrying the default holds.
    private LogicalModelDto ToDto(ILogicalModelMapping row, string? family)
    {
        var resolution = providerDrivers.ResolveProtocolMode(family, row.ProtocolMode);
        var resolved = resolution.TryGetValue(out var protocolMode);

        return new LogicalModelDto(
            row.Id,
            row.Name,
            row.Capability,
            row.ConnectionId,
            row.ConfiguredModelId,
            row.ReasoningEffort,
            resolved ? protocolMode : ProviderDeclaredProtocolModes.Auto)
        {
            UnresolvedProtocolMode = resolved ? null : resolution.Name,
        };
    }

    // A logical model carries no family of its own: the protocol mode on the row belongs to the family of the
    // connection the row maps to, and that is where its spelling is read and written. The identity is taken
    // from the connection row and resolved through the loaded families, so a connection still holding the
    // spelling its family superseded names the same family as one already rewritten. An identity no loaded
    // family claims yields no family at all, and only the host-reserved shapes resolve for such a row.
    private async Task<IReadOnlyDictionary<Guid, string>> FamiliesOfAsync(
        IEnumerable<Guid> connectionIds,
        CancellationToken ct)
    {
        var ids = connectionIds.Distinct().ToArray();
        var identities = await db.AiConnectionProfiles
            .AsNoTracking()
            .Where(profile => ids.Contains(profile.Id))
            .Select(profile => new { profile.Id, profile.ProviderKind })
            .ToListAsync(ct);

        var families = new Dictionary<Guid, string>();
        foreach (var identity in identities)
        {
            if (providerDrivers.ResolveIdentity(identity.ProviderKind).TryGetKey(out var family))
            {
                families[identity.Id] = family;
            }
        }

        return families;
    }

    private async Task<string?> FamilyOfAsync(Guid connectionId, CancellationToken ct)
    {
        var families = await this.FamiliesOfAsync([connectionId], ct);
        return families.TryGetValue(connectionId, out var family) ? family : null;
    }

    // One rule for the protocol mode a logical model carries, matching the one the connection profile applies to
    // the three axes it holds: a stored value the family still answers to is written back exactly as it was
    // read, and only a value naming something else — or nothing, on a create — is replaced by the spelling the
    // family declares now. Rewriting a value that already resolves would undo a family's row migration on the
    // first edit after it, since a row is read through the spellings the family supersedes and would be saved
    // back under the superseded name. A requested shape neither the family nor the host claims is stored as it
    // was submitted, so the read path reports it rather than replacing it with a shape nobody asked for.
    private string ProtocolModeToStore(string? stored, string? family, string requested)
    {
        // Membership is deliberately tolerant below: a shape no loaded family claims is kept as submitted so a
        // family installed later can claim it. Well-formedness is not tolerant. The enum this replaced could
        // not hold an empty value, a thousand characters or a control character, and the column and every reader
        // still cannot.
        // Either spelling is a legitimate submission: the qualified value a family declares, or the unqualified
        // one it supersedes, which a stored row written before the family moved still holds.
        if (!ProviderVocabulary.IsWellFormedQualifiedValue(requested)
            && ProviderVocabulary.ValidateModeName(requested) is { } malformed)
        {
            throw new ArgumentException(malformed.Message, nameof(requested));
        }

        var arriving = providerDrivers.ResolveProtocolMode(family, requested);
        if (!arriving.TryGetValue(out var arrivingMode))
        {
            return requested.Trim();
        }

        return stored is not null
               && providerDrivers.ResolveProtocolMode(family, stored).TryGetValue(out var storedMode)
               && ProviderVocabulary.ValuesEqual(storedMode, arrivingMode)
            ? stored
            : arrivingMode;
    }
}
