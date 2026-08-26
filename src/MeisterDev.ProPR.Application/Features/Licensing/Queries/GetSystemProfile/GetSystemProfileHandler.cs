// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Dtos;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Licensing.Support;

namespace MeisterDev.ProPR.Application.Features.Licensing.Queries.GetSystemProfile;

/// <summary>Reads the installation's observed profile, the changes recorded against it and its host names.</summary>
public sealed class GetSystemProfileHandler(ISystemProfileStore profileStore)
{
    /// <summary>
    ///     How many recorded changes one read returns. An installation that stays where it is records none, so
    ///     the bound only stops a read from growing without limit on one that keeps moving.
    /// </summary>
    private const int MaximumDriftRecords = 100;

    /// <summary>Returns the observed profile with its recorded changes and host names.</summary>
    /// <param name="query">The request.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The profile.</returns>
    public async Task<SystemProfileDto> HandleAsync(
        GetSystemProfileQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var current = await profileStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var drift = await profileStore.ListDriftAsync(MaximumDriftRecords, cancellationToken).ConfigureAwait(false);
        var hostnames = await profileStore.ListHostnamesAsync(cancellationToken).ConfigureAwait(false);

        return new SystemProfileDto(
            current is null ? null : ToDto(current),
            drift.Select(ToDto).ToList().AsReadOnly(),
            hostnames.Select(ToDto).ToList().AsReadOnly());
    }

    private static SystemProfileSnapshotDto ToDto(SystemProfileSnapshot snapshot)
    {
        return new SystemProfileSnapshotDto(
            snapshot.ProfileHash,
            snapshot.CapturedAt,
            snapshot.UpdatedAt,
            new SystemProfileStableComponentsDto(
                snapshot.Stable.PostgresSystemIdentifier,
                snapshot.Stable.DatabaseName,
                snapshot.Stable.DatabaseOid,
                SystemProfileDocument.ToInstant(snapshot.Stable.IdentityCreatedAtUnixSeconds),
                snapshot.Stable.ScmHostHashes),
            new SystemProfileVolatileComponentsDto(
                snapshot.Volatile.OperatingSystem,
                snapshot.Volatile.Runtime,
                snapshot.Volatile.ProcessorCount,
                snapshot.Volatile.TotalAvailableMemoryBytes,
                snapshot.Volatile.TimeZoneId,
                snapshot.Volatile.MachineName));
    }

    private static SystemProfileDriftDto ToDto(SystemProfileDrift drift)
    {
        return new SystemProfileDriftDto(
            drift.OccurredAt,
            drift.ChangedComponents,
            drift.PreviousHash,
            drift.NewHash);
    }

    private static ReplicaHostnameDto ToDto(ReplicaHostname hostname)
    {
        return new ReplicaHostnameDto(hostname.Hostname, hostname.FirstSeenAt, hostname.LastSeenAt);
    }
}
