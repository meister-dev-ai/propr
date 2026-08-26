// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Licensing.Support;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Features.Licensing.Services;

/// <summary>
///     Observes the system the installation runs on and keeps the recorded profile in step with it.
///     <para>
///         The first observation captures the profile. A later one compares the stable components it observed
///         against the recorded ones: unchanged, it refreshes the volatile components and the observed instant;
///         changed, it replaces the profile and records one drift entry naming what moved. Replicas reach the
///         same stable components, so a change is recorded once however many of them observe it.
///     </para>
/// </summary>
public sealed partial class SystemProfileObserver(
    ISystemProfileStore profileStore,
    IDatabaseClusterIdentityProbe databaseProbe,
    ISystemProfileEnvironmentProbe environmentProbe,
    IConfiguredScmHostSource scmHostSource,
    ILicenseStateProvider licenseStateProvider,
    TimeProvider timeProvider,
    ILogger<SystemProfileObserver> logger) : ISystemProfileObserver
{
    /// <inheritdoc />
    public async Task ObserveAsync(DateTimeOffset identityCreatedAt, CancellationToken cancellationToken = default)
    {
        try
        {
            await this.ObserveCoreAsync(identityCreatedAt, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The profile describes the installation and grants nothing, so a failed observation is reported
            // and left for the next one. Whatever the caller was doing continues.
            LogObservationFailed(logger, exception);
        }
    }

    private async Task ObserveCoreAsync(DateTimeOffset identityCreatedAt, CancellationToken cancellationToken)
    {
        var observedAt = timeProvider.GetUtcNow();
        var stable = await this.CollectStableAsync(identityCreatedAt, cancellationToken).ConfigureAwait(false);
        var volatileComponents = environmentProbe.Read();
        var profileHash = SystemProfileDocument.Hash(stable);

        var current = await profileStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        if (current is null)
        {
            await profileStore.TryCaptureAsync(
                    new SystemProfileSnapshot
                    {
                        Stable = stable,
                        Volatile = volatileComponents,
                        ProfileHash = profileHash,
                        CapturedAt = observedAt,
                        UpdatedAt = observedAt,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        else if (string.Equals(current.ProfileHash, profileHash, StringComparison.Ordinal))
        {
            await profileStore.UpdateVolatileAsync(volatileComponents, observedAt, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await this.RecordChangeAsync(current, stable, volatileComponents, profileHash, observedAt, cancellationToken)
                .ConfigureAwait(false);
        }

        if (volatileComponents.MachineName is { Length: > 0 } machineName)
        {
            await profileStore.RecordHostnameAsync(machineName, observedAt, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Replaces the profile and records the change, on the condition that the row still carries the hash
    ///     this observation read. A replica whose write finds the row already moved records nothing, so
    ///     replicas observing the same change together leave one entry rather than one each.
    /// </summary>
    private async Task RecordChangeAsync(
        SystemProfileSnapshot current,
        SystemProfileStableComponents stable,
        SystemProfileVolatileComponents volatileComponents,
        string profileHash,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var changedComponents = SystemProfileDocument.Diff(current.Stable, stable);

        var recorded = await profileStore.TryRecordChangeAsync(
                current.ProfileHash,
                new SystemProfileSnapshot
                {
                    Stable = stable,
                    Volatile = volatileComponents,
                    ProfileHash = profileHash,
                    CapturedAt = current.CapturedAt,
                    UpdatedAt = observedAt,
                },
                new SystemProfileDrift
                {
                    OccurredAt = observedAt,
                    ChangedComponents = changedComponents,
                    PreviousHash = current.ProfileHash,
                    NewHash = profileHash,
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (recorded)
        {
            LogProfileDrifted(logger, string.Join(", ", changedComponents), current.ProfileHash, profileHash);
        }
    }

    private async Task<SystemProfileStableComponents> CollectStableAsync(
        DateTimeOffset identityCreatedAt,
        CancellationToken cancellationToken)
    {
        var databaseIdentity = await databaseProbe.ReadAsync(cancellationToken).ConfigureAwait(false);

        return new SystemProfileStableComponents
        {
            PostgresSystemIdentifier = databaseIdentity.SystemIdentifier,
            DatabaseName = databaseIdentity.DatabaseName,
            DatabaseOid = databaseIdentity.DatabaseOid,
            IdentityCreatedAtUnixSeconds = identityCreatedAt.ToUnixTimeSeconds(),
            ScmHostHashes = await this.CollectHostHashesAsync(cancellationToken).ConfigureAwait(false),
        };
    }

    /// <summary>
    ///     Hashes the configured hosts against the license on file. Without a verified license there is no salt
    ///     to derive, and the component is absent rather than hashed with a fixed one.
    ///     <para>
    ///         The same hosts under a different license identifier hash to different values, so activating,
    ///         renewing onto a new identifier, or removing a license moves this component and is recorded as one
    ///         change. Around a renewal, a replica still holding an older license state in its cache observes
    ///         the previous identifier, so the recorded profile can move back and forth until every replica's
    ///         cache has aged out.
    ///     </para>
    /// </summary>
    private async Task<IReadOnlyList<string>?> CollectHostHashesAsync(CancellationToken cancellationToken)
    {
        var licenseState = await licenseStateProvider.GetStateAsync(cancellationToken).ConfigureAwait(false);
        if (licenseState.Claims?.LicenseId is not { Length: > 0 } licenseId)
        {
            return null;
        }

        var hostBaseUrls = await scmHostSource.ListHostBaseUrlsAsync(cancellationToken).ConfigureAwait(false);

        return ScmHostHash.ComputeSet(hostBaseUrls, licenseId);
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message =
            "The installation's system profile changed ({ChangedComponents}); it now reports {ProfileHash} instead of {PreviousProfileHash}. The profile is descriptive and changes nothing about what the installation is entitled to.")]
    private static partial void LogProfileDrifted(
        ILogger logger,
        string changedComponents,
        string previousProfileHash,
        string profileHash);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "The installation's system profile could not be observed. The recorded profile is unchanged and the next observation records again.")]
    private static partial void LogObservationFailed(ILogger logger, Exception exception);
}
