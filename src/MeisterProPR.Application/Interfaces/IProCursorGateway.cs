// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Dedicated application boundary through which the reviewer backend consumes ProCursor.
/// </summary>
public interface IProCursorGateway
{
    /// <summary>Lists all ProCursor knowledge sources configured for the given client.</summary>
    Task<IReadOnlyList<ProCursorKnowledgeSourceDto>> ListSourcesAsync(Guid clientId, CancellationToken ct = default);

    /// <summary>Registers a new ProCursor knowledge source for the given client.</summary>
    Task<ProCursorKnowledgeSourceDto> CreateSourceAsync(
        Guid clientId,
        ProCursorKnowledgeSourceRegistrationRequest request,
        CancellationToken ct = default);

    /// <summary>Queues a manual refresh or rebuild for the given source.</summary>
    Task<ProCursorIndexJobDto> QueueRefreshAsync(
        Guid clientId,
        Guid sourceId,
        ProCursorRefreshRequest request,
        CancellationToken ct = default);

    /// <summary>Lists tracked branches configured for the given source.</summary>
    Task<IReadOnlyList<ProCursorTrackedBranchDto>> ListTrackedBranchesAsync(
        Guid clientId,
        Guid sourceId,
        CancellationToken ct = default);

    /// <summary>Adds a tracked branch to an existing source.</summary>
    Task<ProCursorTrackedBranchDto> AddTrackedBranchAsync(
        Guid clientId,
        Guid sourceId,
        ProCursorTrackedBranchCreateRequest request,
        CancellationToken ct = default);

    /// <summary>Updates one tracked branch for an existing source.</summary>
    Task<ProCursorTrackedBranchDto?> UpdateTrackedBranchAsync(
        Guid clientId,
        Guid sourceId,
        Guid trackedBranchId,
        ProCursorTrackedBranchUpdateRequest request,
        CancellationToken ct = default);

    /// <summary>Removes one tracked branch from an existing source.</summary>
    Task<bool> RemoveTrackedBranchAsync(
        Guid clientId,
        Guid sourceId,
        Guid trackedBranchId,
        CancellationToken ct = default);

    /// <summary>Executes a reviewer-facing ProCursor knowledge query.</summary>
    Task<ProCursorKnowledgeAnswerDto> AskKnowledgeAsync(
        ProCursorKnowledgeQueryRequest request,
        CancellationToken ct = default);

    /// <summary>Executes a reviewer-facing ProCursor symbol query.</summary>
    Task<ProCursorSymbolInsightDto> GetSymbolInsightAsync(
        ProCursorSymbolQueryRequest request,
        CancellationToken ct = default);
}
