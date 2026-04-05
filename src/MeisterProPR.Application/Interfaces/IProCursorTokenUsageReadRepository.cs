// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Read-side query abstraction for ProCursor token usage reporting.
/// </summary>
public interface IProCursorTokenUsageReadRepository
{
    Task<ProCursorTokenUsageResponse> GetClientUsageAsync(
        Guid clientId,
        DateOnly from,
        DateOnly to,
        ProCursorTokenUsageGranularity granularity,
        string? groupBy,
        CancellationToken ct = default);

    Task<ProCursorSourceTokenUsageResponse?> GetSourceUsageAsync(
        Guid clientId,
        Guid sourceId,
        DateOnly from,
        DateOnly to,
        ProCursorTokenUsageGranularity granularity,
        CancellationToken ct = default);

    Task<IReadOnlyList<ProCursorTopSourceUsageDto>> GetTopSourcesAsync(
        Guid clientId,
        DateOnly from,
        DateOnly to,
        int limit,
        CancellationToken ct = default);

    Task<ProCursorTokenUsageEventsResponse?> GetRecentEventsAsync(
        Guid clientId,
        Guid sourceId,
        int limit,
        CancellationToken ct = default);

    Task<IReadOnlyList<ProCursorTokenUsageExportRowDto>> ExportAsync(
        Guid clientId,
        DateOnly from,
        DateOnly to,
        Guid? sourceId,
        CancellationToken ct = default);

    Task<ProCursorTokenUsageFreshnessResponse> GetFreshnessAsync(Guid clientId, CancellationToken ct = default);
}
