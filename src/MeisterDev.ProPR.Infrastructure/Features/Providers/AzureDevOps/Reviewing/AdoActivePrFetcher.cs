// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using Microsoft.Extensions.Logging;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.WebApi;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

/// <summary>
///     Azure DevOps implementation of <see cref="IActivePullRequestDiscoveryProvider" />.
///     Queries each claimed repository separately. A single project-wide listing is capped, so a busy
///     repository in the same project can fill it and push a claimed repository out.
///     The query's watermark is accepted by the interface but intentionally NOT forwarded to Azure DevOps as a
///     <c>minTime</c> filter: that parameter filters on creation date, not last-update date, so a cutoff would
///     silently drop mentions in long-running pull requests. Per-pull-request comment watermarks in
///     <see cref="MeisterDev.ProPR.Application.Services.MentionScanService" /> handle re-scan deduplication.
/// </summary>
internal sealed class AdoActivePrFetcher(
    VssConnectionFactory connectionFactory,
    IClientScmConnectionRepository connectionRepository,
    ILogger<AdoActivePrFetcher> logger) : ActivePullRequestDiscoveryProviderBase<GitHttpClient>(logger)
{
    /// <summary>How many pull requests one page of a repository's listing asks for.</summary>
    private const int PageSize = 100;

    /// <summary>
    ///     How many pages one repository is read across. Far past what a repository anyone reviews holds open
    ///     at once; it exists so a host that ignores the skip parameter is a bounded read rather than a loop
    ///     that never ends.
    /// </summary>
    private const int MaxPages = 20;

    /// <inheritdoc />
    public override ScmProvider Provider => ScmProvider.AzureDevOps;

    /// <inheritdoc />
    protected override async Task<GitHttpClient> PrepareAsync(ActivePullRequestQuery query, CancellationToken ct)
    {
        var credentials = await AdoProviderAdapterHelpers.ResolveCredentialsAsync(
            connectionRepository,
            query.ClientId == Guid.Empty ? null : query.ClientId,
            query.ScopePath,
            ct);
        var connection = await connectionFactory.GetConnectionAsync(query.ScopePath, credentials, ct);
        return await connection.GetClientAsync<GitHttpClient>(ct);
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<ActivePullRequestRef>> ListRepositoryAsync(
        GitHttpClient gitClient,
        ActivePullRequestQuery query,
        ClaimedRepositoryRef repository,
        CancellationToken ct)
    {
        var criteria = new GitPullRequestSearchCriteria
        {
            Status = PullRequestStatus.Active,
        };

        var refs = new List<ActivePullRequestRef>();
        var reachedPageLimit = true;

        for (var page = 0; page < MaxPages; page++)
        {
            var pullRequests = await gitClient.GetPullRequestsAsync(
                repository.RepositoryId,
                criteria,
                maxCommentLength: null,
                skip: page * PageSize,
                top: PageSize,
                userState: null,
                cancellationToken: ct);

            foreach (var pullRequest in pullRequests)
            {
                refs.Add(
                    new ActivePullRequestRef(
                        query.ScopePath,
                        pullRequest.Repository?.ProjectReference?.Name ?? string.Empty,

                        // The claimed identifier, not the one the provider echoes. The scan matches what it
                        // reads back against the claim, and Azure DevOps spells a repository id in lower case
                        // where a configuration may hold it in any case.
                        repository.RepositoryId,
                        pullRequest.PullRequestId,

                        // The Azure DevOps client does not expose a last-update date on a pull request, so
                        // this is an upper bound. It makes the pull-request-level skip in the scan fall
                        // through to comparing comment timestamps, which is the accurate check anyway.
                        DateTimeOffset.UtcNow));
            }

            if (pullRequests.Count < PageSize)
            {
                reachedPageLimit = false;
                break;
            }
        }

        if (reachedPageLimit)
        {
            ActivePullRequestDiscoveryLog.PageLimitReached(
                logger,
                this.Provider,
                repository.RepositoryId,
                MaxPages);
        }

        return refs;
    }

    /// <summary>
    ///     Azure DevOps wraps a throttled response in its own exception type, so the shared HTTP check alone
    ///     would miss it.
    /// </summary>
    protected override bool IsThrottled(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is VssServiceResponseException { HttpStatusCode: HttpStatusCode.TooManyRequests })
            {
                return true;
            }
        }

        return base.IsThrottled(exception);
    }
}
