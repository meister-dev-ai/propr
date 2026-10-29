// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Globalization;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;

/// <summary>Reads and stores the review execution limits on the tenant record.</summary>
public sealed class TenantReviewExecutionLimitsStore(MeisterProPRDbContext dbContext, TimeProvider? clock = null)
    : ITenantReviewExecutionLimitsStore
{
    /// <summary>The event type of the audit entry a change of the limits writes.</summary>
    public const string AuditEventType = "tenant.review_limits.updated";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<TenantReviewExecutionLimits?> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        return await dbContext.Tenants
            .AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => new TenantReviewExecutionLimits(tenant.MaxConcurrentReviews, tenant.MaxReviewDurationSeconds))
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TenantReviewExecutionLimits?> SetAsync(
        Guid tenantId,
        TenantReviewExecutionLimits limits,
        Guid? actorUserId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.EnsureValid();

        var tenant = await dbContext.Tenants.SingleOrDefaultAsync(row => row.Id == tenantId, ct).ConfigureAwait(false);
        if (tenant is null)
        {
            return null;
        }

        var previous = new TenantReviewExecutionLimits(tenant.MaxConcurrentReviews, tenant.MaxReviewDurationSeconds);
        if (previous == limits)
        {
            // A platform that sends the same values on every synchronization would otherwise fill the audit history
            // of the tenant with entries that record no change.
            return previous;
        }

        tenant.MaxConcurrentReviews = limits.MaxConcurrentReviews;
        tenant.MaxReviewDurationSeconds = limits.MaxReviewDurationSeconds;

        // The change and its audit entry are saved in one call, so every change that takes effect has its entry.
        dbContext.TenantAuditEntries.Add(
            new TenantAuditEntryRecord
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                ActorUserId = actorUserId,
                EventType = AuditEventType,
                Summary = $"Set the review limits of tenant '{tenant.DisplayName}'.",
                Detail = $"maxConcurrentReviews={Format(limits.MaxConcurrentReviews)} (was {Format(previous.MaxConcurrentReviews)}); "
                         + $"maxReviewDurationSeconds={Format(limits.MaxReviewDurationSeconds)} (was {Format(previous.MaxReviewDurationSeconds)})",
                OccurredAt = this._clock.GetUtcNow(),
            });
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

        return limits;
    }

    private static string Format(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "no limit";
}
