// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Infrastructure.Services;

/// <summary>
///     Rebuilds ProCursor token usage rollups for a selected client and interval.
/// </summary>
public sealed class ProCursorTokenUsageRebuildService(IProCursorTokenUsageAggregationService aggregationService)
    : IProCursorTokenUsageRebuildService
{
    public async Task<ProCursorTokenUsageRebuildResponse> RebuildAsync(
        Guid clientId,
        ProCursorTokenUsageRebuildRequest request,
        CancellationToken ct = default)
    {
        var recomputedBucketCount = await aggregationService.RefreshAsync(
            request.From,
            request.To,
            clientId,
            request.IncludeMonthly,
            ct);

        return new ProCursorTokenUsageRebuildResponse(
            request.From,
            request.To,
            recomputedBucketCount,
            DateTimeOffset.UtcNow);
    }
}
