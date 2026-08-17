// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

internal sealed class ProviderPullRequestFetcher(
    IEnumerable<IProviderPullRequestFetcher> providerFetchers,
    IClientScmConnectionRepository? connectionRepository = null) : IPullRequestFetcher
{
    private readonly IReadOnlyDictionary<ScmProvider, IProviderPullRequestFetcher> _providerFetchersByProvider =
        providerFetchers.ToDictionary(fetcher => fetcher.Provider);

    public async Task<PullRequestRef> FetchRefAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        var provider = await this.ResolveProviderAsync(organizationUrl, clientId, cancellationToken);
        if (!this._providerFetchersByProvider.TryGetValue(provider, out var fetcher))
        {
            throw new InvalidOperationException($"No pull-request fetcher is registered for provider {provider}.");
        }

        return await fetcher.FetchRefAsync(organizationUrl, projectId, repositoryId, pullRequestId, clientId, cancellationToken);
    }

    public async Task<PullRequest> FetchAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        int? compareToIterationId = null,
        Guid? clientId = null,
        CancellationToken cancellationToken = default,
        ReviewRevision? compareToReviewRevision = null,
        IReviewRepositoryWorkspace? workspace = null)
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
            cancellationToken,
            compareToReviewRevision,
            workspace);
    }

    public async Task<ChangedFile?> FetchFileDiffAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        string filePath,
        int? compareToIterationId = null,
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        var provider = await this.ResolveProviderAsync(organizationUrl, clientId, cancellationToken);
        if (!this._providerFetchersByProvider.TryGetValue(provider, out var fetcher))
        {
            throw new InvalidOperationException($"No pull-request fetcher is registered for provider {provider}.");
        }

        return await fetcher.FetchFileDiffAsync(
            organizationUrl,
            projectId,
            repositoryId,
            pullRequestId,
            iterationId,
            filePath,
            compareToIterationId,
            clientId,
            cancellationToken);
    }

    public async Task<PullRequest> FetchThreadContextAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        Guid? clientId = null,
        CancellationToken cancellationToken = default,
        bool includeChangedFileManifest = false)
    {
        var provider = await this.ResolveProviderAsync(organizationUrl, clientId, cancellationToken);
        if (!this._providerFetchersByProvider.TryGetValue(provider, out var fetcher))
        {
            throw new InvalidOperationException($"No pull-request fetcher is registered for provider {provider}.");
        }

        return await fetcher.FetchThreadContextAsync(
            organizationUrl,
            projectId,
            repositoryId,
            pullRequestId,
            iterationId,
            clientId,
            cancellationToken,
            includeChangedFileManifest);
    }

    public async Task<IReadOnlyList<PrCommentThread>> FetchThreadsAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        var provider = await this.ResolveProviderAsync(organizationUrl, clientId, cancellationToken);
        if (!this._providerFetchersByProvider.TryGetValue(provider, out var fetcher))
        {
            throw new InvalidOperationException($"No pull-request fetcher is registered for provider {provider}.");
        }

        return await fetcher.FetchThreadsAsync(organizationUrl, projectId, repositoryId, pullRequestId, clientId, cancellationToken);
    }

    public async Task<IReadOnlyList<PrCommentThread>> FetchConversationThreadsAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        Guid? clientId = null,
        CancellationToken cancellationToken = default)
    {
        var provider = await this.ResolveProviderAsync(organizationUrl, clientId, cancellationToken);
        if (!this._providerFetchersByProvider.TryGetValue(provider, out var fetcher))
        {
            throw new InvalidOperationException($"No pull-request fetcher is registered for provider {provider}.");
        }

        return await fetcher.FetchConversationThreadsAsync(
            organizationUrl,
            projectId,
            repositoryId,
            pullRequestId,
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
