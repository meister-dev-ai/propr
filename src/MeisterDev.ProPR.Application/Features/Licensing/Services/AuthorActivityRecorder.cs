// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;

namespace MeisterDev.ProPR.Application.Features.Licensing.Services;

/// <summary>
///     Applies the exclusion rules to an observation and writes it to the rollup.
/// </summary>
/// <remarks>
///     Two of the three signal classes are decided from the observation alone by
///     <see cref="AutomationAuthorPolicy" />. The third, the identities ProPR is configured to act as, is a
///     database read, and it runs only when the first two found nothing, which keeps the read off the path for
///     an author already identified as automation.
/// </remarks>
public sealed class AuthorActivityRecorder(
    IAuthorActivityRollupStore rollupStore,
    IConfiguredReviewerIdentitySource configuredReviewerIdentities) : IAuthorActivityRecorder
{
    /// <inheritdoc />
    public async Task RecordAsync(
        AuthorActivityObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);

        var excluded = AutomationAuthorPolicy.IsAutomation(observation)
                       || await this.IsOwnIdentityAsync(observation, cancellationToken).ConfigureAwait(false);

        await rollupStore
            .RecordAuthorAsync(
                observation.Host,
                observation.ExternalUserId,
                observation.Source,
                excluded,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Returns whether the observed account is one of the reviewer identities ProPR is configured to act as
    ///     on the same host.
    /// </summary>
    /// <remarks>
    ///     Compared on the host-scoped key rather than on the raw identifier, which is the same rule the rollup
    ///     keys rows by. Both sides come from the one host, so the comparison is between two identifiers that
    ///     host issued.
    /// </remarks>
    private async Task<bool> IsOwnIdentityAsync(
        AuthorActivityObservation observation,
        CancellationToken cancellationToken)
    {
        var configured = await configuredReviewerIdentities
            .ListExternalUserIdsAsync(observation.Host, cancellationToken)
            .ConfigureAwait(false);

        if (configured.Count == 0)
        {
            return false;
        }

        var authorKey = observation.Host.ScopedKey(observation.ExternalUserId.Trim());

        return configured.Any(externalUserId =>
            string.Equals(
                observation.Host.ScopedKey(externalUserId.Trim()),
                authorKey,
                StringComparison.Ordinal));
    }
}
