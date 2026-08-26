// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Licensing;

namespace MeisterDev.ProPR.Application.Features.Licensing.Services;

/// <summary>
///     Resolves the effective ceiling for one quota from the activated license and the community values.
///     <para>
///         Licensed values apply while the installation is on the commercial edition, which covers the active,
///         warning and grace stages: an expiry does not tighten a ceiling until the grace window has closed as
///         well. Outside that, and for a dimension the license in force leaves out, the community value
///         applies. An absent limit therefore never reads as unlimited.
///     </para>
///     <para>
///         Nothing here is compared against live installation state. This answers what the ceiling is; counting
///         what the installation currently holds and deciding whether one more fits belongs to the enforcement
///         site that asked.
///     </para>
/// </summary>
public sealed class LicenseLimitResolver(
    ILicenseStateProvider licenseStateProvider,
    ILicensingCapabilityService capabilityService) : ILicenseLimitResolver
{
    /// <summary>Resolves the effective ceiling for one quota.</summary>
    /// <param name="key">The dimension to resolve.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The ceiling, where it came from, and the stage it was resolved under.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The key is not one this resolver knows.</exception>
    public async Task<LicenseLimitResolution> ResolveAsync(
        LicenseLimitKey key,
        CancellationToken cancellationToken = default)
    {
        var licenseState = await licenseStateProvider.GetStateAsync(cancellationToken).ConfigureAwait(false);

        // Whether a license state amounts to the commercial edition is asked of the license state rather than
        // restated here, so the stages the licensed values apply in stay the ones capability resolution uses.
        if (licenseState.Edition != InstallationEdition.Commercial)
        {
            return CommunityCeilingFor(key, licenseState.Stage);
        }

        // A state that amounts to the commercial edition carries claims, and the fallback covers the same case
        // as a license that states nothing for this dimension.
        var stated = StatedLimitFor(licenseState.Claims?.Limits ?? LicenseLimits.None, key);
        if (stated.IsAbsent)
        {
            return CommunityCeilingFor(key, licenseState.Stage);
        }

        // The licensed number of concurrent reviews counts only while the capability that allows more than one
        // review at a time is available. Without it the installation runs one at a time whatever the license
        // says, and resolving the licensed number here would let an enforcement site admit work the review
        // pipeline then refuses to run in parallel.
        if (key == LicenseLimitKey.ConcurrentReviews
            && !await capabilityService
                .IsEnabledAsync(PremiumCapabilityKey.ParallelReviewExecution, cancellationToken)
                .ConfigureAwait(false))
        {
            return CommunityCeilingFor(key, licenseState.Stage);
        }

        return LicensedCeilingFor(key, stated, licenseState.Stage);
    }

    /// <summary>
    ///     What every installation is held to without a license in force.
    ///     <para>
    ///         Every dimension has an answer here, so the resolver never has to report that it does not know
    ///         one. Runners resolve to zero although the capability gate refuses an enrollment before any count
    ///         is consulted: a refusal quotes the ceiling, so it must not name a number that gate does not
    ///         enforce.
    ///     </para>
    /// </summary>
    private static LicenseLimitResolution CommunityCeilingFor(LicenseLimitKey key, LicenseStage stage)
    {
        return key switch
        {
            LicenseLimitKey.AuthorsPerMonth =>
                LicenseLimitResolution.Unmetered(key, LicenseLimitSource.Community, stage),
            LicenseLimitKey.Clients =>
                LicenseLimitResolution.Unlimited(key, LicenseLimitSource.Community, stage),
            LicenseLimitKey.Runners =>
                LicenseLimitResolution.Of(key, 0, LicenseLimitSource.Community, stage),

            // The number comes from the clamp the review pipeline already applies without the parallel-execution
            // capability, so the ceiling an enforcement site reads and the width a host runs at stay the same
            // number.
            LicenseLimitKey.ConcurrentReviews => LicenseLimitResolution.Of(
                key,
                ReviewConcurrencyPolicy.Unlicensed,
                LicenseLimitSource.Community,
                stage),
            _ => throw new ArgumentOutOfRangeException(
                nameof(key),
                key,
                "No community value is defined for this limit."),
        };
    }

    private static LicenseLimitResolution LicensedCeilingFor(
        LicenseLimitKey key,
        LicenseLimit stated,
        LicenseStage stage)
    {
        // A stated limit that is neither a non-negative whole number nor the unlimited marker is refused when
        // the document is read, in LicenseReader, so a limit arriving here is one of the two and needs no
        // second check. An absent one has already been answered from the community values.
        return stated.TryGetCount(out var count)
            ? LicenseLimitResolution.Of(key, count, LicenseLimitSource.License, stage)
            : LicenseLimitResolution.Unlimited(key, LicenseLimitSource.License, stage);
    }

    private static LicenseLimit StatedLimitFor(LicenseLimits limits, LicenseLimitKey key)
    {
        return key switch
        {
            LicenseLimitKey.AuthorsPerMonth => limits.AuthorsPerMonth,
            LicenseLimitKey.Clients => limits.Clients,
            LicenseLimitKey.Runners => limits.Runners,
            LicenseLimitKey.ConcurrentReviews => limits.ConcurrentReviews,
            _ => throw new ArgumentOutOfRangeException(
                nameof(key),
                key,
                "No license limit member is defined for this limit."),
        };
    }
}
