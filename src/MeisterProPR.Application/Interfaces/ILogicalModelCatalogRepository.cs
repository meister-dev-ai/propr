// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Persistence contract for logical models: named model roles stored at a tenant-catalog scope with a per-client
///     override layer. A client override of the same name shadows the tenant entry for that client; the system tenant
///     stores per-client overrides only. Resolution precedence (override before tenant), capability typing, and
///     referential-integrity blocks are layered on top of this store by later work — this contract is storage and
///     scoped reads only.
/// </summary>
public interface ILogicalModelCatalogRepository
{
    /// <summary>
    ///     Adds a tenant-catalog logical model. Rejects the system tenant (which has no tenant-catalog layer) and a name
    ///     already used by another tenant entry in the same tenant.
    /// </summary>
    /// <exception cref="MeisterProPR.Application.Exceptions.SystemTenantLogicalModelCatalogException">
    ///     The tenant is the system tenant (or the empty tenant), which stores per-client overrides only.
    /// </exception>
    /// <exception cref="MeisterProPR.Application.Exceptions.DuplicateLogicalModelException">
    ///     A tenant entry with the same name already exists in this tenant.
    /// </exception>
    Task AddTenantEntryAsync(Guid tenantId, LogicalModelDto entry, CancellationToken ct);

    /// <summary>
    ///     Adds a per-client override logical model. Rejects a name already used by another override for the same
    ///     client. An override may use a name that has no tenant-catalog entry.
    /// </summary>
    /// <exception cref="MeisterProPR.Application.Exceptions.DuplicateLogicalModelException">
    ///     An override with the same name already exists for this client.
    /// </exception>
    Task AddClientOverrideAsync(Guid clientId, LogicalModelDto entry, CancellationToken ct);

    /// <summary>
    ///     Updates a tenant-catalog entry's mapping (capability, connection, configured model, reasoning effort,
    ///     protocol) by name. The name is the immutable key (use rename to change it). Returns false if absent.
    /// </summary>
    /// <exception cref="MeisterProPR.Application.Exceptions.LogicalModelReferenceInvalidException">
    ///     The new mapping references a missing connection/model or a model that cannot serve the capability.
    /// </exception>
    Task<bool> UpdateTenantEntryAsync(Guid tenantId, string name, LogicalModelDto entry, CancellationToken ct);

    /// <summary>Updates a per-client override's mapping by name. Returns false if absent.</summary>
    /// <exception cref="MeisterProPR.Application.Exceptions.LogicalModelReferenceInvalidException">
    ///     The new mapping references a missing connection/model or a model that cannot serve the capability.
    /// </exception>
    Task<bool> UpdateClientOverrideAsync(Guid clientId, string name, LogicalModelDto entry, CancellationToken ct);

    /// <summary>Returns every tenant-catalog logical model for the given tenant, ordered by name.</summary>
    Task<IReadOnlyList<LogicalModelDto>> GetTenantEntriesAsync(Guid tenantId, CancellationToken ct);

    /// <summary>
    ///     Returns the tenant-catalog logical models visible to the given client, resolved via the client's tenant. A
    ///     client on the system tenant (or an unknown client) yields an empty list.
    /// </summary>
    Task<IReadOnlyList<LogicalModelDto>> GetTenantEntriesForClientAsync(Guid clientId, CancellationToken ct);

    /// <summary>Returns every per-client override logical model for the given client, ordered by name.</summary>
    Task<IReadOnlyList<LogicalModelDto>> GetClientOverridesAsync(Guid clientId, CancellationToken ct);

    /// <summary>Deletes the named tenant-catalog logical model. Returns <see langword="false" /> if not found.</summary>
    Task<bool> DeleteTenantEntryAsync(Guid tenantId, string name, CancellationToken ct);

    /// <summary>Deletes the named per-client override logical model. Returns <see langword="false" /> if not found.</summary>
    Task<bool> DeleteClientOverrideAsync(Guid clientId, string name, CancellationToken ct);

    /// <summary>
    ///     Renames a tenant-catalog logical model. Returns <see langword="false" /> if <paramref name="oldName" /> is
    ///     not found; throws <see cref="MeisterProPR.Application.Exceptions.DuplicateLogicalModelException" /> if
    ///     <paramref name="newName" /> is already used in the tenant.
    /// </summary>
    Task<bool> RenameTenantEntryAsync(Guid tenantId, string oldName, string newName, CancellationToken ct);

    /// <summary>
    ///     Renames a per-client override logical model. Returns <see langword="false" /> if <paramref name="oldName" />
    ///     is not found; throws <see cref="MeisterProPR.Application.Exceptions.DuplicateLogicalModelException" /> if
    ///     <paramref name="newName" /> is already used for the client.
    /// </summary>
    Task<bool> RenameClientOverrideAsync(Guid clientId, string oldName, string newName, CancellationToken ct);

    /// <summary>
    ///     Returns the logical-model name mapped to <paramref name="purpose" /> for the client, or
    ///     <see langword="null" /> when the purpose is unmapped (resolution then falls back to the client's AI purpose
    ///     bindings).
    /// </summary>
    Task<string?> GetPurposeRoleAsync(Guid clientId, AiPurpose purpose, CancellationToken ct);

    /// <summary>Returns every purpose → logical-model-name mapping for the client.</summary>
    Task<IReadOnlyDictionary<AiPurpose, string>> GetPurposeRolesAsync(Guid clientId, CancellationToken ct);

    /// <summary>Upserts the logical-model mapping for one purpose of one client.</summary>
    Task SetPurposeRoleAsync(Guid clientId, AiPurpose purpose, string logicalModelName, CancellationToken ct);

    /// <summary>Removes the logical-model mapping for one purpose. Returns <see langword="false" /> if none existed.</summary>
    Task<bool> RemovePurposeRoleAsync(Guid clientId, AiPurpose purpose, CancellationToken ct);
}
