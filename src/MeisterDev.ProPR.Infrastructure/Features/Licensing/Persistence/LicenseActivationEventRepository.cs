// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Licensing.Persistence;

/// <summary>EF Core-backed record of the license changes an installation has made.</summary>
public sealed class LicenseActivationEventRepository(MeisterProPRDbContext dbContext) : ILicenseActivationEventStore
{
    /// <inheritdoc />
    public async Task RecordAsync(
        LicenseActivationEvent activationEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activationEvent);

        dbContext.LicenseActivationEvents.Add(
            new LicenseActivationEventRecord
            {
                Id = Guid.CreateVersion7(),
                Action = activationEvent.Action,
                OccurredAt = activationEvent.OccurredAt,
                ActorUserId = activationEvent.ActorUserId,
                LicenseId = activationEvent.LicenseId,
                Licensee = activationEvent.Licensee,
            });

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LicenseActivationEvent>> ListRecentAsync(
        int maxEvents,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEvents);

        // The identity is part of the ordering so that two records carrying the same instant come back in the
        // same order on every read, rather than in whichever order the database happens to return them.
        var records = await dbContext.LicenseActivationEvents
            .AsNoTracking()
            .OrderByDescending(record => record.OccurredAt)
            .ThenByDescending(record => record.Id)
            .Take(maxEvents)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return records.Select(ToEvent).ToList().AsReadOnly();
    }

    private static LicenseActivationEvent ToEvent(LicenseActivationEventRecord record)
    {
        return new LicenseActivationEvent
        {
            Action = record.Action,
            OccurredAt = record.OccurredAt,
            ActorUserId = record.ActorUserId,
            LicenseId = record.LicenseId,
            Licensee = record.Licensee,
        };
    }
}
