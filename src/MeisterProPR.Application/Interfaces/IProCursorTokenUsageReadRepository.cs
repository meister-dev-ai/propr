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
    /// <summary>
    ///     Gets client token usage statistics for a specified date range.
    /// </summary>
    Task<ProCursorTokenUsageResponse> GetClientUsageAsync(
        Guid clientId,
        DateOnly from,
        DateOnly to,
        ProCursorTokenUsageGranularity granularity,
        string? groupBy,
        CancellationToken ct = default);

    /// <summary>
    ///     Gets token usage for a specific source within a client.
    /// </summary>
    Task<ProCursorSourceTokenUsageResponse?> GetSourceUsageAsync(
        Guid clientId,
        Guid sourceId,
        DateOnly from,
        DateOnly to,
        ProCursorTokenUsageGranularity granularity,
        CancellationToken ct = default);

    /// <summary>
    ///     Gets the top sources by token usage for a client.
    /// </summary>
    Task<IReadOnlyList<ProCursorTopSourceUsageDto>> GetTopSourcesAsync(
        Guid clientId,
        DateOnly from,
        DateOnly to,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    ///     Gets recent token usage events for a specific source.
    /// </summary>
    Task<ProCursorTokenUsageEventsResponse?> GetRecentEventsAsync(
        Guid clientId,
        Guid sourceId,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    ///     Exports token usage data for a client within a date range.
    /// </summary>
    Task<IReadOnlyList<ProCursorTokenUsageExportRowDto>> ExportAsync(
        Guid clientId,
        DateOnly from,
        DateOnly to,
        Guid? sourceId,
        CancellationToken ct = default);

    /// <summary>
    ///     Gets the freshness information of token usage data for a client.
    /// </summary>
    Task<ProCursorTokenUsageFreshnessResponse> GetFreshnessAsync(Guid clientId, CancellationToken ct = default);
}
