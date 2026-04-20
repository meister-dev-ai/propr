// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Providers.Common;

internal sealed class ProviderPullRequestFetcher(
    IEnumerable<IProviderPullRequestFetcher> providerFetchers,
    IClientScmConnectionRepository? connectionRepository = null) : IPullRequestFetcher
{
    private readonly IReadOnlyDictionary<ScmProvider, IProviderPullRequestFetcher> _providerFetchersByProvider =
        providerFetchers.ToDictionary(fetcher => fetcher.Provider);

    public async Task<PullRequest> FetchAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        int? compareToIterationId = null,
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        var provider = await this.ResolveProviderAsync(organizationUrl, clientId, cancellationToken);
        if (!this._providerFetchersByProvider.TryGetValue(provider, out var fetcher))
        {
            throw new InvalidOperationException($"No pull-request fetcher is registered for provider {provider}.");
        }

        return await fetcher.FetchAsync(
            organizationUrl,
            projectId,
            repositoryId,
            pullRequestId,
            iterationId,
            compareToIterationId,
            clientId,
            cancellationToken);
    }

    private async Task<ScmProvider> ResolveProviderAsync(string organizationUrl, Guid? clientId, CancellationToken ct)
    {
        return await ProviderResolutionUtilities.ResolveProviderAsync(
            organizationUrl,
            clientId,
            connectionRepository,
            ct);
    }
}
