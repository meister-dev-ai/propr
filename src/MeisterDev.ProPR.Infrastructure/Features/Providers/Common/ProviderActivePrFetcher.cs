// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>
///     Resolves active pull-request discovery by the provider the query names and dispatches to that
///     provider's adapter.
/// </summary>
/// <remarks>
///     A configuration for a provider this deployment has no adapter for is reported rather than handled by
///     another provider's code. Silently falling back would reach a foreign host with the wrong client, which
///     is what this seam exists to stop.
/// </remarks>
internal sealed class ProviderActivePrFetcher(IEnumerable<IActivePullRequestDiscoveryProvider> discoveryProviders) : IActivePrFetcher
{
    private readonly IReadOnlyDictionary<ScmProvider, IActivePullRequestDiscoveryProvider> _byProvider =
        discoveryProviders.ToDictionary(provider => provider.Provider);

    public async Task<ActivePullRequestDiscovery> GetRecentlyUpdatedPullRequestsAsync(
        ActivePullRequestQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!this._byProvider.TryGetValue(query.Provider, out var discoveryProvider))
        {
            throw new InvalidOperationException($"No active pull-request discovery is registered for provider {query.Provider}.");
        }

        return await discoveryProvider.GetRecentlyUpdatedPullRequestsAsync(query, cancellationToken);
    }
}
