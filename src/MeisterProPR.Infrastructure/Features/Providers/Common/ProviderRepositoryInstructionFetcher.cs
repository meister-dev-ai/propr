// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Providers.Common;

internal sealed class ProviderRepositoryInstructionFetcher(
    IEnumerable<IProviderRepositoryInstructionFetcher> providerFetchers,
    IClientScmConnectionRepository? connectionRepository = null) : IRepositoryInstructionFetcher
{
    private readonly IReadOnlyDictionary<ScmProvider, IProviderRepositoryInstructionFetcher>
        _providerFetchersByProvider =
            providerFetchers.ToDictionary(fetcher => fetcher.Provider);

    public async Task<IReadOnlyList<RepositoryInstruction>> FetchAsync(
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
            : [];
    }
}
