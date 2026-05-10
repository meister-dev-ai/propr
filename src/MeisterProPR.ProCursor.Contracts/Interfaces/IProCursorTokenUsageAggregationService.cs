// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Builds and refreshes ProCursor token usage rollups from raw captured events.
/// </summary>
public interface IProCursorTokenUsageAggregationService
{
    /// <summary>
    ///     Refreshes token usage aggregations for the specified date range.
    /// </summary>
    /// <param name="from">The start date for the refresh.</param>
    /// <param name="to">The end date for the refresh.</param>
    /// <param name="clientId">Optional client ID to filter the refresh.</param>
    /// <param name="includeMonthly">Whether to include monthly aggregations.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of aggregations refreshed.</returns>
    Task<int> RefreshAsync(
        DateOnly from,
        DateOnly to,
        Guid? clientId = null,
        bool includeMonthly = true,
        CancellationToken ct = default);

    /// <summary>
    ///     Refreshes recent token usage aggregations.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of aggregations refreshed.</returns>
    Task<int> RefreshRecentAsync(CancellationToken ct = default);
}
