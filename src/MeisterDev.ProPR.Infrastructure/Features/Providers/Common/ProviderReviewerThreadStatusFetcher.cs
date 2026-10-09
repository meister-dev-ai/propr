// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

internal sealed class ProviderReviewerThreadStatusFetcher(
    IEnumerable<IProviderReviewerThreadStatusFetcher> providerFetchers,
    IClientScmConnectionRepository? connectionRepository = null,
    IEnumerable<IScmConnectionConfigurationPolicy>? configurationPolicies = null) : IReviewerThreadStatusFetcher
{
    private readonly IReadOnlyDictionary<ScmProvider, IProviderReviewerThreadStatusFetcher>
        _providerFetchersByProvider =
            providerFetchers.ToDictionary(fetcher => fetcher.Provider);

    public async Task<IReadOnlyList<PrThreadStatusEntry>> GetReviewerThreadStatusesAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        ThreadOwnershipResolver ownership,
        Guid clientId,
        CancellationToken ct = default)
    {
        var provider = await this.ResolveProviderAsync(organizationUrl, clientId, ct);
        if (!this._providerFetchersByProvider.TryGetValue(provider, out var fetcher))
        {
            throw new InvalidOperationException($"No reviewer thread status fetcher is registered for provider {provider}.");
        }

        return await fetcher.GetReviewerThreadStatusesAsync(
            organizationUrl,
            projectId,
            repositoryId,
            pullRequestId,
            ownership,
            clientId,
            ct);
    }

    private Task<ScmProvider> ResolveProviderAsync(string organizationUrl, Guid clientId, CancellationToken ct)
    {
        return ProviderResolutionUtilities.ResolveProviderAsync(
            organizationUrl, clientId, connectionRepository, ct, configurationPolicies, "reviewer thread status");
    }
}
