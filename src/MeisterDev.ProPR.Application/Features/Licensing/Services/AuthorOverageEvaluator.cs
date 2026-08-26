// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Licensing;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Application.Features.Licensing.Services;

/// <summary>
///     Compares the current calendar month's counted authors against the number the license states for them,
///     records a month that goes above it, and reports the comparison.
///     <para>
///         The number compared against is read from the license document's own claims rather than through the
///         limit resolver. The resolver is what enforcement points read, and keeping this comparison off it
///         means the reporting path and the enforcement path share no call. A license that leaves the limit
///         out, one that states it as unlimited, and an installation with no license in force all have no
///         number to compare against and are left alone.
///     </para>
///     <para>
///         Nothing reads what this produces to decide whether work runs, and what keeps the allowance out of
///         enforcement is that no enforcement point resolves the author dimension at all. The resolver does not
///         report that dimension as unmetered in general: it does so on the community path, while a license
///         that states a count resolves to that count. Intake, claiming and dispatch resolve the client, runner
///         and concurrent-review keys, and the author key is resolved only by the administration read that
///         reports it. No path from an arriving pull request to an executing review therefore passes through
///         the author count or the recorded overage. The record and the report exist for an operator and for a
///         renewal conversation.
///     </para>
/// </summary>
public sealed partial class AuthorOverageEvaluator(
    ILicenseStateProvider licenseStateProvider,
    IAuthorActivityRollupStore rollupStore,
    IAuthorOverageStore overageStore,
    ILogger<AuthorOverageEvaluator> logger) : IAuthorOverageEvaluator
{
    /// <inheritdoc />
    public async Task<AuthorOverageState?> EvaluateAsync(
        AuthorMonthCount? observedAuthors = null,
        CancellationToken cancellationToken = default)
    {
        var licenseState = await licenseStateProvider.GetStateAsync(cancellationToken).ConfigureAwait(false);

        // Whether a license is in force is asked of the license state rather than restated here, so the stages
        // this comparison runs in stay the ones the rest of licensing treats as commercial: the running term
        // and the grace window after it.
        if (licenseState.Edition != InstallationEdition.Commercial)
        {
            return null;
        }

        var stated = licenseState.Claims?.Limits.AuthorsPerMonth ?? LicenseLimit.Absent;
        if (!stated.TryGetCount(out var licensedCount))
        {
            return null;
        }

        // The count and the month it was taken for come from one read, and both travel to the write. The
        // record is keyed by month and its stored count only rises, so a count attributed to the wrong month
        // cannot be corrected afterwards.
        var observed = observedAuthors
                       ?? ToAuthorMonthCount(await rollupStore.GetCurrentMonthCountsAsync(cancellationToken).ConfigureAwait(false));

        if (observed.AuthorCount <= licensedCount)
        {
            return new AuthorOverageState(licensedCount, observed.AuthorCount, IsInOverage: false);
        }

        await this.RecordAsync(observed.Month, licensedCount, observed.AuthorCount, cancellationToken)
            .ConfigureAwait(false);

        return new AuthorOverageState(licensedCount, observed.AuthorCount, IsInOverage: true);
    }

    /// <summary>The counted authors of one month, without the excluded identities the read also reports.</summary>
    private static AuthorMonthCount ToAuthorMonthCount(AuthorActivityMonthCounts counts) =>
        new(counts.Month, counts.Counted);

    /// <summary>
    ///     Records the month and reports it once, on the write that inserted the month's row.
    ///     <para>
    ///         The insert is what the report is taken from because it happens once per month across every
    ///         replica, while the evaluation runs on each lifecycle sweep and on each administration read.
    ///         Reporting on the comparison instead would repeat the same line for as long as the month stayed
    ///         above the number.
    ///     </para>
    ///     <para>
    ///         A failed write is reported and left for the next evaluation. The record decides nothing, so the
    ///         comparison the caller was given stands either way.
    ///     </para>
    /// </summary>
    private async Task RecordAsync(
        DateOnly month,
        long licensedCount,
        long observedCount,
        CancellationToken cancellationToken)
    {
        try
        {
            var firstObservation = await overageStore
                .RecordAsync(month, licensedCount, observedCount, cancellationToken)
                .ConfigureAwait(false);

            if (firstObservation)
            {
                LogOverageObserved(logger, observedCount, licensedCount);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRecordFailed(logger, exception);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "This month's counted pull request authors ({ObservedCount}) are above the {LicensedCount} the license states. Nothing has been withheld, delayed or degraded: the author allowance is recorded and reported, not enforced. Arrange a license that covers the number of authors this installation has.")]
    private static partial void LogOverageObserved(ILogger logger, long observedCount, long licensedCount);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "The author allowance overage could not be recorded; the next evaluation records again. Nothing has been withheld, delayed or degraded, and nothing about what the installation may run depends on the record.")]
    private static partial void LogRecordFailed(ILogger logger, Exception exception);
}
