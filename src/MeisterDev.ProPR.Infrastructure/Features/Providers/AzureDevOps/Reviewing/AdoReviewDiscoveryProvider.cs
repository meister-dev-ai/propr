// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using static MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support.AdoProviderAdapterHelpers;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Security;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

internal sealed partial class AdoReviewDiscoveryProvider(
    IClientScmConnectionRepository connectionRepository,
    IClientScmScopeRepository scopeRepository,
    VssConnectionFactory connectionFactory,
    ILogger<AdoReviewDiscoveryProvider>? logger = null) : IReviewDiscoveryProvider
{
    private readonly ILogger<AdoReviewDiscoveryProvider> _logger = logger ?? NullLogger<AdoReviewDiscoveryProvider>.Instance;
    internal Func<string, CancellationToken, Task<GitHttpClient>>? GitClientResolver { get; set; }

    public ScmProvider Provider => ScmProvider.AzureDevOps;

    public async Task<IReadOnlyList<ReviewDiscoveryItemDto>> ListOpenReviewsAsync(
        Guid clientId,
        RepositoryRef repository,
        ReviewerIdentity? reviewer,
        CancellationToken ct = default,
        ReviewDiscoveryContext? context = null)
    {
        EnsureAzureDevOps(repository.Host);

        var projectId = ResolveProjectId(repository);
        var itemsByNumber = new Dictionary<int, ReviewDiscoveryItemDto>();
        AdoConnectionCredentials? selectedCredentials = null;
        IReadOnlyList<string> organizationUrls;
        if (context is null)
        {
            organizationUrls = await ResolveOrganizationUrlsAsync(
                connectionRepository,
                scopeRepository,
                clientId,
                repository.Host,
                ct).ConfigureAwait(false);
        }
        else
        {
            var selectedConnection = await ManualReviewDiscoveryCredentials.ResolveAsync(
                    connectionRepository, clientId, repository.Host, context, new AdoReviewSourcePolicy().IsSelectedScopeCompatible, ct)
                .ConfigureAwait(false);
            if (new Uri(selectedConnection.HostBaseUrl).AbsolutePath == "/")
            {
                var scopes = await scopeRepository.GetByConnectionIdAsync(clientId, selectedConnection.Id, ct).ConfigureAwait(false);
                if (!scopes.Any(scope => scope.ClientId == clientId && scope.ConnectionId == selectedConnection.Id && scope.IsEnabled &&
                                         string.Equals(scope.ScopeType, "organization", StringComparison.OrdinalIgnoreCase) &&
                                         string.Equals(
                                             scope.ScopePath.TrimEnd('/'), context.ProviderScopePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException("The selected connection has no enabled organization scope for this review target.");
                }
            }

            selectedCredentials = ToAdoCredentials(selectedConnection)
                                  ?? throw new InvalidOperationException("The selected connection is unavailable for this review target.");
            organizationUrls = [context.ProviderScopePath.TrimEnd('/')];
        }

        var querySucceeded = false;
        foreach (var organizationUrl in organizationUrls)
        {
            try
            {
                GitHttpClient gitClient;
                if (context is null)
                {
                    gitClient = await ResolveGitClientAsync(
                        connectionFactory,
                        connectionRepository,
                        this.GitClientResolver,
                        clientId,
                        organizationUrl,
                        ct).ConfigureAwait(false);
                }
                else if (this.GitClientResolver is not null)
                {
                    gitClient = await this.GitClientResolver(organizationUrl, ct).ConfigureAwait(false);
                }
                else
                {
                    var connection = await connectionFactory.GetOverviewConnectionAsync(organizationUrl, selectedCredentials!, ct).ConfigureAwait(false);
                    gitClient = await connection.GetClientAsync<GitHttpClient>(ct).ConfigureAwait(false);
                }

                var criteria = new GitPullRequestSearchCriteria
                {
                    Status = PullRequestStatus.Active,
                };

                if (reviewer is not null && Guid.TryParse(reviewer.ExternalUserId, out var reviewerId))
                {
                    criteria.ReviewerId = reviewerId;
                }

                var pullRequests = await gitClient.GetPullRequestsAsync(
                    projectId,
                    repository.ExternalRepositoryId,
                    criteria,
                    top: 100,
                    userState: null,
                    cancellationToken: ct).ConfigureAwait(false);

                foreach (var pullRequest in pullRequests.Where(pr => MatchesRepository(pr, repository))
                             .Take(context is null ? int.MaxValue : 100))
                {
                    if (reviewer is not null && !ContainsReviewer(pullRequest, reviewer))
                    {
                        continue;
                    }

                    ReviewRevision? revision = null;
                    try
                    {
                        revision = await GetLatestRevisionAsync(
                            gitClient,
                            projectId,
                            repository.ExternalRepositoryId,
                            pullRequest.PullRequestId,
                            ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (context is not null && ex is not OperationCanceledException && !ct.IsCancellationRequested)
                    {
                        if (AdoReadFailures.IsThrottled(ex))
                        {
                            throw new ProviderThrottledException();
                        }

                        if (AdoReadFailures.DeniedStatus(ex) is { } status)
                        {
                            throw AdoReadFailures.Denial(status, AdoReadFailures.IsConnectionDenied(ex));
                        }

                        LogRevisionUnavailable(this._logger, clientId, pullRequest.PullRequestId);
                    }

                    var item = ToDiscoveryItem(
                        repository,
                        pullRequest,
                        revision,
                        SelectRequestedReviewer(repository.Host, pullRequest), organizationUrl);
                    itemsByNumber[pullRequest.PullRequestId] = context is not null && pullRequest.IsDraft == true
                        ? item with { ReviewState = CodeReviewState.Draft }
                        : item;
                }

                querySucceeded = true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && (context is null || ex is not OperationCanceledException))
            {
                if (context is not null)
                {
                    if (AdoReadFailures.IsThrottled(ex))
                    {
                        throw new ProviderThrottledException();
                    }

                    if (AdoReadFailures.DeniedStatus(ex) is { } status)
                    {
                        throw AdoReadFailures.Denial(status, AdoReadFailures.IsConnectionDenied(ex));
                    }
                }

                LogCandidateUnavailable(this._logger, clientId);
            }
        }

        if (context is not null && !querySucceeded)
        {
            throw new InvalidOperationException("The provider could not list open pull requests.");
        }

        return itemsByNumber.Values
            .OrderBy(item => item.CodeReview.Number)
            .ToList()
            .AsReadOnly();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Azure DevOps review discovery candidate failed for client {ClientId}.")]
    private static partial void LogCandidateUnavailable(ILogger logger, Guid clientId);

    [LoggerMessage(
        Level = LogLevel.Warning, Message = "Azure DevOps review discovery revision is unavailable for client {ClientId}, pull request {PullRequestId}.")]
    private static partial void LogRevisionUnavailable(ILogger logger, Guid clientId, int pullRequestId);

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
