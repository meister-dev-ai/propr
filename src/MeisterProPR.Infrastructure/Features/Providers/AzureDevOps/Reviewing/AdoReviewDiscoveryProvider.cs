// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using static MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Support.AdoProviderAdapterHelpers;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

internal sealed class AdoReviewDiscoveryProvider(
    IClientScmConnectionRepository connectionRepository,
    IClientScmScopeRepository scopeRepository,
    VssConnectionFactory connectionFactory) : IReviewDiscoveryProvider
{
    internal Func<string, CancellationToken, Task<GitHttpClient>>? GitClientResolver { get; set; }

    public ScmProvider Provider => ScmProvider.AzureDevOps;

    public async Task<IReadOnlyList<ReviewDiscoveryItemDto>> ListOpenReviewsAsync(
        Guid clientId,
        RepositoryRef repository,
        ReviewerIdentity? reviewer,
        CancellationToken ct = default)
    {
        EnsureAzureDevOps(repository.Host);

        var projectId = ResolveProjectId(repository);
        var itemsByNumber = new Dictionary<int, ReviewDiscoveryItemDto>();
        foreach (var organizationUrl in await ResolveOrganizationUrlsAsync(
                     connectionRepository,
                     scopeRepository,
                     clientId,
                     repository.Host,
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
                var criteria = new GitPullRequestSearchCriteria
                {
                    Status = PullRequestStatus.Active,
                };

                if (reviewer is not null && Guid.TryParse(reviewer.ExternalUserId, out var reviewerId))
                {
                    criteria.ReviewerId = reviewerId;
                }

                var pullRequests = await gitClient.GetPullRequestsByProjectAsync(
                    projectId,
                    criteria,
                    top: 100,
                    userState: null,
                    cancellationToken: ct);

                foreach (var pullRequest in pullRequests.Where(pr => MatchesRepository(pr, repository)))
                {
                    if (reviewer is not null && !ContainsReviewer(pullRequest, reviewer))
                    {
                        continue;
                    }

                    var revision = await GetLatestRevisionAsync(
                        gitClient,
                        projectId,
                        repository.ExternalRepositoryId,
                        pullRequest.PullRequestId,
                        ct);
                    itemsByNumber[pullRequest.PullRequestId] = ToDiscoveryItem(
                        repository,
                        pullRequest,
                        revision,
                        SelectRequestedReviewer(repository.Host, pullRequest));
                }
            }
            catch when (!ct.IsCancellationRequested)
            {
            }
        }

        return itemsByNumber.Values
            .OrderBy(item => item.CodeReview.Number)
            .ToList()
            .AsReadOnly();
    }

    private static bool MatchesRepository(GitPullRequest pullRequest, RepositoryRef repository)
    {
        return string.Equals(
            pullRequest.Repository?.Id.ToString(),
            repository.ExternalRepositoryId,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsReviewer(GitPullRequest pullRequest, ReviewerIdentity reviewer)
    {
        return pullRequest.Reviewers?.Any(candidate =>
            string.Equals(candidate.Id, reviewer.ExternalUserId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.UniqueName, reviewer.Login, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.DisplayName, reviewer.DisplayName, StringComparison.OrdinalIgnoreCase)) == true;
    }
}
