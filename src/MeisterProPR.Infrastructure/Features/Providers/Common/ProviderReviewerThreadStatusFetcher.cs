// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Features.Providers.Common;

internal sealed class ProviderReviewerThreadStatusFetcher(
    IEnumerable<IProviderReviewerThreadStatusFetcher> providerFetchers,
    IClientScmConnectionRepository? connectionRepository = null) : IReviewerThreadStatusFetcher
{
    private readonly IReadOnlyDictionary<ScmProvider, IProviderReviewerThreadStatusFetcher>
        _providerFetchersByProvider =
            providerFetchers.ToDictionary(fetcher => fetcher.Provider);

    public async Task<IReadOnlyList<PrThreadStatusEntry>> GetReviewerThreadStatusesAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid reviewerId,
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
            reviewerId,
            clientId,
            ct);
    }

    private async Task<ScmProvider> ResolveProviderAsync(string organizationUrl, Guid clientId, CancellationToken ct)
    {
        if (connectionRepository is null)
        {
            return ScmProvider.AzureDevOps;
        }

        var normalizedHostBaseUrl = NormalizeHostBaseUrl(organizationUrl);
        var matchingProviders = (await connectionRepository.GetByClientIdAsync(clientId, ct))
            .Where(connection => connection.IsActive)
            .Where(connection => string.Equals(
                connection.HostBaseUrl,
                normalizedHostBaseUrl,
                StringComparison.OrdinalIgnoreCase))
            .Select(connection => connection.ProviderFamily)
            .Distinct()
            .ToList();

        if (matchingProviders.Count == 1)
        {
            return matchingProviders[0];
        }

        if (matchingProviders.Count > 1)
        {
            if (LooksLikeAzureDevOpsScope(organizationUrl) && matchingProviders.Contains(ScmProvider.AzureDevOps))
            {
                return ScmProvider.AzureDevOps;
            }

            throw new InvalidOperationException(
                $"Multiple active SCM providers share host {normalizedHostBaseUrl} for client {clientId}. The reviewer thread status provider is ambiguous.");
        }

        if (LooksLikeAzureDevOpsScope(organizationUrl))
        {
            return ScmProvider.AzureDevOps;
        }

        throw new InvalidOperationException($"No active SCM provider connection matched host {normalizedHostBaseUrl} for client {clientId}.");
    }

    private static bool LooksLikeAzureDevOpsScope(string organizationUrl)
    {
        if (!Uri.TryCreate(organizationUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return string.Equals(uri.Host, "dev.azure.com", StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHostBaseUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Provider scope must be an absolute URL.", nameof(value));
        }

        var builder = new UriBuilder(uri)
        {
            Path = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };

        return builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }
}
