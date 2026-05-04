// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     Offline ProCursor gateway that reports reviewer-facing knowledge tools as unavailable.
/// </summary>
public sealed class NoOpProCursorGateway : IProCursorGateway
{
    public Task<IReadOnlyList<ProCursorKnowledgeSourceDto>> ListSourcesAsync(Guid clientId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ProCursorKnowledgeSourceDto>>([]);

    public Task<ProCursorKnowledgeSourceDto> CreateSourceAsync(
        Guid clientId,
        ProCursorKnowledgeSourceRegistrationRequest request,
        CancellationToken ct = default)
        => throw new NotSupportedException("Offline Reviewing composition does not manage ProCursor sources.");

    public Task<ProCursorIndexJobDto> QueueRefreshAsync(
        Guid clientId,
        Guid sourceId,
        ProCursorRefreshRequest request,
        CancellationToken ct = default)
        => throw new NotSupportedException("Offline Reviewing composition does not queue ProCursor refresh jobs.");

    public Task<IReadOnlyList<ProCursorTrackedBranchDto>> ListTrackedBranchesAsync(
        Guid clientId,
        Guid sourceId,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ProCursorTrackedBranchDto>>([]);

    public Task<ProCursorTrackedBranchDto> AddTrackedBranchAsync(
        Guid clientId,
        Guid sourceId,
        ProCursorTrackedBranchCreateRequest request,
        CancellationToken ct = default)
        => throw new NotSupportedException("Offline Reviewing composition does not manage ProCursor tracked branches.");

    public Task<ProCursorTrackedBranchDto?> UpdateTrackedBranchAsync(
        Guid clientId,
        Guid sourceId,
        Guid trackedBranchId,
        ProCursorTrackedBranchUpdateRequest request,
        CancellationToken ct = default)
        => Task.FromResult<ProCursorTrackedBranchDto?>(null);

    public Task<bool> RemoveTrackedBranchAsync(
        Guid clientId,
        Guid sourceId,
        Guid trackedBranchId,
        CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<ProCursorKnowledgeAnswerDto> AskKnowledgeAsync(
        ProCursorKnowledgeQueryRequest request,
        CancellationToken ct = default)
    {
        return Task.FromResult(
            new ProCursorKnowledgeAnswerDto(
                "unavailable",
                [],
                "ProCursor knowledge retrieval is unavailable in offline review evaluation mode."));
    }

    public Task<ProCursorSymbolInsightDto> GetSymbolInsightAsync(
        ProCursorSymbolQueryRequest request,
        CancellationToken ct = default)
    {
        return Task.FromResult(
            new ProCursorSymbolInsightDto(
                "unavailable",
                null,
                false,
                false,
                null,
                []));
    }
}
