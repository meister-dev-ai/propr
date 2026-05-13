// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Infrastructure.Features.ProCursor.Remote;

/// <summary>
///     Gateway used when ProCursor is intentionally not configured for the current environment.
/// </summary>
public sealed class DisabledProCursorGateway : IProCursorGateway
{
    private const string DisabledMessage =
        "ProCursor is not configured for this environment. Configure PROCURSOR_SERVICE_BASE_URL and PROCURSOR_SHARED_KEY to enable the extracted service.";

    public Task<IReadOnlyList<ProCursorKnowledgeSourceDto>> ListSourcesAsync(Guid clientId, CancellationToken ct = default)
    {
        throw new ProCursorDependencyUnavailableException(DisabledMessage);
    }

    public Task<ProCursorKnowledgeSourceDto> CreateSourceAsync(
        Guid clientId,
        ProCursorKnowledgeSourceRegistrationRequest request,
        CancellationToken ct = default)
    {
        throw new ProCursorDependencyUnavailableException(DisabledMessage);
    }

    public Task<ProCursorIndexJobDto> QueueRefreshAsync(
        Guid clientId,
        Guid sourceId,
        ProCursorRefreshRequest request,
        CancellationToken ct = default)
    {
        throw new ProCursorDependencyUnavailableException(DisabledMessage);
    }

    public Task<IReadOnlyList<ProCursorTrackedBranchDto>> ListTrackedBranchesAsync(
        Guid clientId,
        Guid sourceId,
        CancellationToken ct = default)
    {
        throw new ProCursorDependencyUnavailableException(DisabledMessage);
    }

    public Task<ProCursorTrackedBranchDto> AddTrackedBranchAsync(
        Guid clientId,
        Guid sourceId,
        ProCursorTrackedBranchCreateRequest request,
        CancellationToken ct = default)
    {
        throw new ProCursorDependencyUnavailableException(DisabledMessage);
    }

    public Task<ProCursorTrackedBranchDto?> UpdateTrackedBranchAsync(
        Guid clientId,
        Guid sourceId,
        Guid trackedBranchId,
        ProCursorTrackedBranchUpdateRequest request,
        CancellationToken ct = default)
    {
        throw new ProCursorDependencyUnavailableException(DisabledMessage);
    }

    public Task<bool> RemoveTrackedBranchAsync(
        Guid clientId,
        Guid sourceId,
        Guid trackedBranchId,
        CancellationToken ct = default)
    {
        throw new ProCursorDependencyUnavailableException(DisabledMessage);
    }

    public Task<ProCursorKnowledgeAnswerDto> AskKnowledgeAsync(
        ProCursorKnowledgeQueryRequest request,
        CancellationToken ct = default)
    {
        return Task.FromResult(new ProCursorKnowledgeAnswerDto("unavailable", [], DisabledMessage));
    }

    public Task<ProCursorSymbolInsightDto> GetSymbolInsightAsync(
        ProCursorSymbolQueryRequest request,
        CancellationToken ct = default)
    {
        return Task.FromResult(new ProCursorSymbolInsightDto("unavailable", null, false, false, null, []));
    }
}
