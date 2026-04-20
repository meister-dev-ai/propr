// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Features.Providers.Common;

internal sealed class ProviderRepositoryExclusionFetcher(
    IEnumerable<IProviderRepositoryExclusionFetcher> providerFetchers,
    IClientScmConnectionRepository? connectionRepository = null) : IRepositoryExclusionFetcher
{
    private readonly IReadOnlyDictionary<ScmProvider, IProviderRepositoryExclusionFetcher> _providerFetchersByProvider =
        providerFetchers.ToDictionary(fetcher => fetcher.Provider);

    public async Task<ReviewExclusionRules> FetchAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        string targetBranch,
        Guid? clientId,
        CancellationToken cancellationToken)
    {
        var provider = await ProviderResolutionUtilities.ResolveProviderAsync(
            organizationUrl,
            clientId,
            connectionRepository,
            cancellationToken);

        return this._providerFetchersByProvider.TryGetValue(provider, out var fetcher)
            ? await fetcher.FetchAsync(
                organizationUrl,
                projectId,
                repositoryId,
                targetBranch,
                clientId,
                cancellationToken)
            : ReviewExclusionRules.Default;
    }
}
