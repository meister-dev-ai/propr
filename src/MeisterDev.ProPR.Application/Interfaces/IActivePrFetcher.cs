// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Discovers the recently updated active pull requests a mention scan should read, whichever provider
///     the configuration names.
/// </summary>
/// <remarks>
///     Implemented by a composite that resolves the provider named in the query and hands the work to that
///     provider's <see cref="IActivePullRequestDiscoveryProvider" />, the same way pull-request fetching is
///     resolved.
/// </remarks>
public interface IActivePrFetcher
{
    /// <summary>Discovers the active pull requests updated at or after the query's watermark.</summary>
    /// <param name="query">What to ask, and which provider to ask.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>What the tick found, and whether it covered every repository the query claimed.</returns>
    /// <exception cref="InvalidOperationException">
    ///     No discovery implementation is registered for the query's provider. Reported rather than handled
    ///     by another provider's code, which would reach a foreign host with the wrong client.
    /// </exception>
    Task<ActivePullRequestDiscovery> GetRecentlyUpdatedPullRequestsAsync(
        ActivePullRequestQuery query,
        CancellationToken cancellationToken = default);
}
