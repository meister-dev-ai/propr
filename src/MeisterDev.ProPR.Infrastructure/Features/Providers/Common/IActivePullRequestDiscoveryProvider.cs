// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>
///     Discovers active pull requests on one provider family.
/// </summary>
/// <remarks>
///     One implementation per provider, each registered under this interface. The application layer sees only
///     <see cref="MeisterDev.ProPR.Application.Interfaces.IActivePrFetcher" />, whose single implementation
///     (<see cref="ProviderActivePrFetcher" />) selects the implementation matching the provider named in the
///     query. The two interfaces declare the same method for that reason: one is the provider-neutral entry
///     point, the other the per-provider adapter behind it. This mirrors the pair
///     <see cref="IProviderPullRequestFetcher" /> forms with
///     <see cref="MeisterDev.ProPR.Application.Interfaces.IPullRequestFetcher" />.
/// </remarks>
internal interface IActivePullRequestDiscoveryProvider
{
    /// <summary>The provider family this adapter implements.</summary>
    ScmProvider Provider { get; }

    /// <summary>Discovers the active pull requests updated at or after the query's watermark.</summary>
    /// <remarks>
    ///     Returns whether the tick covered every claimed repository as well as what it found. A partial
    ///     result has the same shape as a complete one, and the caller advances a watermark over it.
    /// </remarks>
    Task<ActivePullRequestDiscovery> GetRecentlyUpdatedPullRequestsAsync(
        ActivePullRequestQuery query,
        CancellationToken cancellationToken = default);
}
