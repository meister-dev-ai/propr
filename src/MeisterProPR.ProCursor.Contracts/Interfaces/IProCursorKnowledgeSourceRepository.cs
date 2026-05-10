// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Persistence abstraction for ProCursor knowledge-source catalog records.
/// </summary>
public interface IProCursorKnowledgeSourceRepository
{
    /// <summary>Lists all enabled sources across all clients for scheduler polling.</summary>
    Task<IReadOnlyList<ProCursorKnowledgeSource>> ListEnabledAsync(CancellationToken ct = default);

    /// <summary>Lists all configured sources for one client.</summary>
    Task<IReadOnlyList<ProCursorKnowledgeSource>> ListByClientAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>Returns one source by its identifier regardless of client scope.</summary>
    Task<ProCursorKnowledgeSource?> GetBySourceIdAsync(Guid sourceId, CancellationToken ct = default);

    /// <summary>Returns one source by its identifier, scoped to the owning client.</summary>
    Task<ProCursorKnowledgeSource?> GetByIdAsync(Guid clientId, Guid sourceId, CancellationToken ct = default);

    /// <summary>Returns the configured source that matches the given repository coordinates.</summary>
    Task<ProCursorKnowledgeSource?> GetByRepositoryAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        CancellationToken ct = default);

    /// <summary>Returns whether the client already has a source with the same unique coordinates.</summary>
    Task<bool> ExistsAsync(
        Guid clientId,
        ProCursorSourceKind sourceKind,
        string organizationUrl,
        string projectId,
        string repositoryId,
        string? rootPath,
        CancellationToken ct = default);

    /// <summary>Persists a newly created knowledge source.</summary>
    Task AddAsync(ProCursorKnowledgeSource source, CancellationToken ct = default);

    /// <summary>Persists changes to an existing knowledge source.</summary>
    Task UpdateAsync(ProCursorKnowledgeSource source, CancellationToken ct = default);

    /// <summary>Deletes the specified tracked branch if it belongs to the given source and client.</summary>
    Task<bool> DeleteTrackedBranchAsync(
        Guid clientId,
        Guid sourceId,
        Guid trackedBranchId,
        CancellationToken ct = default);
}
