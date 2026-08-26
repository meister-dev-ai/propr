// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Licensing.Ports;

/// <summary>
///     Reads the reviewer identities ProPR is configured to act as, across every client and connection.
/// </summary>
/// <remarks>
///     These are the accounts the product itself posts and answers under. Work ProPR did for one of them is
///     work it did for itself, so the account is not a counted author. The read spans clients because the
///     count is installation-wide, while the configuration is per connection.
/// </remarks>
public interface IConfiguredReviewerIdentitySource
{
    /// <summary>
    ///     Returns the provider-native identifier of every configured reviewer identity on one host.
    /// </summary>
    /// <remarks>
    ///     Scoped to the host rather than global, because a provider-native identifier names one account only
    ///     within the host that issued it. Identities on connections that are switched off are included: the
    ///     account is still the one ProPR acts as, and switching a connection off does not turn it into a
    ///     person.
    /// </remarks>
    /// <param name="host">The host to read identities for.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The identifiers, empty when the host has none configured.</returns>
    Task<IReadOnlyList<string>> ListExternalUserIdsAsync(
        ProviderHostRef host,
        CancellationToken cancellationToken = default);
}
