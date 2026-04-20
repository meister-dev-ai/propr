// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using static MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Support.AdoProviderAdapterHelpers;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

internal sealed class AdoCodeReviewQueryService(
    IClientScmConnectionRepository connectionRepository,
    IClientScmScopeRepository scopeRepository,
    VssConnectionFactory connectionFactory) : ICodeReviewQueryService
{
    internal Func<string, CancellationToken, Task<GitHttpClient>>? GitClientResolver { get; set; }

    public ScmProvider Provider => ScmProvider.AzureDevOps;

    public async Task<ReviewDiscoveryItemDto?> GetReviewAsync(
        Guid clientId,
        CodeReviewRef review,
        CancellationToken ct = default)
    {
        EnsureAzureDevOps(review.Repository.Host);

        var projectId = ResolveProjectId(review.Repository);
        foreach (var organizationUrl in await ResolveOrganizationUrlsAsync(
                     connectionRepository,
                     scopeRepository,
                     clientId,
                     review.Repository.Host,
                     ct))
        {
            try
            {
                var gitClient = await ResolveGitClientAsync(
                    connectionFactory,
                    connectionRepository,
                    this.GitClientResolver,
                    clientId,
                    organizationUrl,
                    ct);
                var pullRequest = await gitClient.GetPullRequestAsync(
                    projectId,
                    review.Repository.ExternalRepositoryId,
                    review.Number,
                    cancellationToken: ct);
                var revision = await AdoProviderAdapterHelpers.GetLatestRevisionAsync(
                    gitClient,
                    projectId,
                    review.Repository.ExternalRepositoryId,
                    review.Number,
                    ct);

                return ToDiscoveryItem(
                    review.Repository,
                    pullRequest,
                    revision,
                    SelectRequestedReviewer(review.Repository.Host, pullRequest));
            }
            catch when (!ct.IsCancellationRequested)
            {
            }
        }

        return null;
    }

    public async Task<ReviewRevision?> GetLatestRevisionAsync(
        Guid clientId,
        CodeReviewRef review,
        CancellationToken ct = default)
    {
        var item = await this.GetReviewAsync(clientId, review, ct);
        return item?.ReviewRevision;
    }
}
