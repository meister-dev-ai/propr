// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using static MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support.AdoProviderAdapterHelpers;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

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
                // This organization doesn't have the pull request; try the next candidate organization URL.
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
