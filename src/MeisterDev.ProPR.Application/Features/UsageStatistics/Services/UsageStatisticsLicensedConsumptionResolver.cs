// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Models;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Features.UsageStatistics.Services;

/// <summary>
///     Reads what a commercial installation reports about its license and its current consumption.
///     <para>
///         The read is skipped unless the license state amounts to the commercial edition, so a community
///         installation makes no consumption read for a ping and carries none of these values.
///     </para>
///     <para>
///         Every failure ends in an absent report rather than an exception. The ping is descriptive and
///         nothing in licensing reads it, so a licensing read that fails must not stop the snapshot that would
///         have carried it: the snapshot is sent without these values and the next cycle reads again.
///     </para>
/// </summary>
public sealed partial class UsageStatisticsLicensedConsumptionResolver(
    ILogger<UsageStatisticsLicensedConsumptionResolver> logger,
    ILicenseStateProvider? licenseStateProvider = null,
    ILicensingIdentityStore? licensingIdentityStore = null,
    ISystemProfileStore? systemProfileStore = null,
    ILicensedResourceCountSource? resourceCountSource = null,
    IConcurrentReviewPeakStore? concurrentReviewPeakStore = null,
    IAuthorActivityRollupStore? authorActivityRollupStore = null)
{
    /// <summary>
    ///     Returns what this installation reports about its license, or <see langword="null" /> when it
    ///     reports nothing: no licensing module, no license in force, or a read that failed.
    /// </summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public async Task<UsageStatisticsLicensedConsumption?> ResolveAsync(CancellationToken cancellationToken = default)
    {
        if (licenseStateProvider is not { } stateProvider
            || licensingIdentityStore is not { } identityStore
            || resourceCountSource is not { } countSource)
        {
            // Without the licensing module the installation has no license state, no licensing identity and no
            // counts, which is the same position as an installation whose license is not in force.
            return null;
        }

        try
        {
            return await ReadAsync(
                    stateProvider,
                    identityStore,
                    systemProfileStore,
                    countSource,
                    concurrentReviewPeakStore,
                    authorActivityRollupStore,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller asked for the work to stop, so the cancellation is reported rather than turned into a
            // failed read.
            throw;
        }
        catch (Exception exception)
        {
            // Everything else ends in an absent report, including a cancellation raised while this token was
            // not cancelled, such as a timeout inside one of the reads. The caller has already claimed the send
            // attempt by the time the snapshot is built, and an attempt with no recorded outcome holds the
            // interval, so an exception leaving here would cost the whole report rather than these values.
            LogReadFailed(logger, exception);
            return null;
        }
    }

    /// <summary>
    ///     Reads the license state first and stops there unless the installation is on the commercial edition,
    ///     so the identity, the profile and the counts are queried only for an installation that reports them.
    ///     <para>
    ///         A partial read is reported as no read. Consumption is attributed to the license identifier and
    ///         the licensing identity, so counts arriving without them could not be placed against any
    ///         installation under any license. The one value that stands on its own is the profile hash, which
    ///         is absent on an installation whose profile has not been captured yet. That is a measurement the
    ///         installation does not have rather than one that failed, and the two identifiers still place the
    ///         report. The previous day's concurrent-review peak is absent on the same footing, which is an
    ///         installation that recorded no executing review on that day, and so is the current month's
    ///         author count on an installation that keeps no author rollup.
    ///     </para>
    /// </summary>
    private static async Task<UsageStatisticsLicensedConsumption?> ReadAsync(
        ILicenseStateProvider stateProvider,
        ILicensingIdentityStore identityStore,
        ISystemProfileStore? profileStore,
        ILicensedResourceCountSource countSource,
        IConcurrentReviewPeakStore? concurrentReviewPeakStore,
        IAuthorActivityRollupStore? authorActivityRollupStore,
        CancellationToken cancellationToken)
    {
        var licenseState = await stateProvider.GetStateAsync(cancellationToken).ConfigureAwait(false);

        if (licenseState.Edition != InstallationEdition.Commercial
            || licenseState.Claims?.LicenseId is not { Length: > 0 } licenseId)
        {
            return null;
        }

        var licensingIdentity = await identityStore.GetOrCreateAsync(cancellationToken).ConfigureAwait(false);

        var profile = profileStore is null
            ? null
            : await profileStore.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        var counts = await countSource.GetCountsAsync(cancellationToken).ConfigureAwait(false);

        var peakConcurrentReviews = concurrentReviewPeakStore is null
            ? null
            : await concurrentReviewPeakStore.GetPreviousDayPeakAsync(cancellationToken).ConfigureAwait(false);

        // The counted authors of the current month, read together with the identities the exclusion rules kept
        // out so both come from one captured month. Only the counted ones are reported; the excluded ones are
        // an operator-facing number and describe no consumption.
        var authorCounts = authorActivityRollupStore is null
            ? null
            : await authorActivityRollupStore.GetCurrentMonthCountsAsync(cancellationToken).ConfigureAwait(false);

        return new UsageStatisticsLicensedConsumption
        {
            LicenseId = licenseId,
            LicensingIdentity = licensingIdentity,
            SystemProfileHash = profile?.ProfileHash,
            Clients = counts.Clients,
            EnrolledRunners = counts.EnrolledRunners,
            PeakConcurrentReviewsPreviousDay = peakConcurrentReviews,
            AuthorsCurrentMonth = authorCounts?.Counted,
        };
    }

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message =
            "The licensing values for this installation's usage statistics snapshot could not be read. The snapshot is sent without them and the next cycle reads again.")]
    private static partial void LogReadFailed(ILogger logger, Exception exception);
}
