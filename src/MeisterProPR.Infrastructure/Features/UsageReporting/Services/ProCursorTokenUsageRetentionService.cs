// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.Services;

/// <summary>
///     Applies the configured retention policy for ProCursor token reporting data.
/// </summary>
public sealed class ProCursorTokenUsageRetentionService(
    MeisterProPRDbContext db,
    IOptions<ProCursorTokenUsageOptions> options) : IProCursorTokenUsageRetentionService
{
    private readonly ProCursorTokenUsageOptions _options = options.Value;

    public async Task<ProCursorTokenUsageRetentionResult> PurgeExpiredAsync(CancellationToken ct = default)
    {
        var eventThreshold = DateTimeOffset.UtcNow.AddDays(-this._options.EventRetentionDays);
        var rollupThreshold = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-this._options.RollupRetentionDays));

        var expiredEvents = await db.ProCursorTokenUsageEvents
            .Where(item => item.OccurredAtUtc < eventThreshold)
            .ToListAsync(ct);
        var expiredRollups = await db.ProCursorTokenUsageRollups
            .Where(item => item.BucketStartDate < rollupThreshold)
            .ToListAsync(ct);

        if (expiredEvents.Count > 0)
        {
            db.ProCursorTokenUsageEvents.RemoveRange(expiredEvents);
        }

        if (expiredRollups.Count > 0)
        {
            db.ProCursorTokenUsageRollups.RemoveRange(expiredRollups);
        }

        await db.SaveChangesAsync(ct);

        return new ProCursorTokenUsageRetentionResult(
            expiredEvents.Count,
            expiredRollups.Count,
            DateTimeOffset.UtcNow);
    }
}
