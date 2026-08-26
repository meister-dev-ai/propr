// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Dtos;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;

namespace MeisterDev.ProPR.Application.Features.Licensing.Queries.GetLicenseActivationHistory;

/// <summary>Reads the installation's recorded license changes, newest first.</summary>
public sealed class GetLicenseActivationHistoryHandler(ILicenseActivationEventStore activationEventStore)
{
    /// <summary>
    ///     How many records one read returns. An installation records one per operator action, so the whole
    ///     history is well inside this on any real install and the bound only stops a read from growing without
    ///     limit.
    /// </summary>
    private const int MaximumEvents = 200;

    /// <summary>Returns the recorded license changes, newest first.</summary>
    /// <param name="query">The request.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The recorded changes.</returns>
    public async Task<IReadOnlyList<LicenseActivationEventDto>> HandleAsync(
        GetLicenseActivationHistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var events = await activationEventStore.ListRecentAsync(MaximumEvents, cancellationToken)
            .ConfigureAwait(false);

        return events.Select(ToDto).ToList().AsReadOnly();
    }

    private static LicenseActivationEventDto ToDto(LicenseActivationEvent activationEvent)
    {
        return new LicenseActivationEventDto(
            activationEvent.Action,
            activationEvent.OccurredAt,
            activationEvent.ActorUserId,
            activationEvent.LicenseId,
            activationEvent.Licensee);
    }
}
