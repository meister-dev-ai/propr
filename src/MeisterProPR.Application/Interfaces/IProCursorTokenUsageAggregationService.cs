// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Builds and refreshes ProCursor token usage rollups from raw captured events.
/// </summary>
public interface IProCursorTokenUsageAggregationService
{
    Task<int> RefreshAsync(
        DateOnly from,
        DateOnly to,
        Guid? clientId = null,
        bool includeMonthly = true,
        CancellationToken ct = default);

    Task<int> RefreshRecentAsync(CancellationToken ct = default);
}
