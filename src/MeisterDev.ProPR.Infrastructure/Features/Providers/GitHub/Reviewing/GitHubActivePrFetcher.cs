// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

/// <summary>
///     GitHub implementation of <see cref="IActivePullRequestDiscoveryProvider" />.
/// </summary>
/// <remarks>
///     GitHub's pull-request listing has no "updated since" filter, so the listing is sorted by update time
///     descending and the read stops at the first entry older than the watermark. That makes the usual case one
///     request per repository however long the repository's history is.
///     The host comes from the client's connection rather than being assumed, so GitHub Enterprise Server is
///     reached at its own address.
/// </remarks>
internal sealed class GitHubActivePrFetcher(
    GitHubConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory,
    ILogger<GitHubActivePrFetcher> logger)
    : ActivePullRequestDiscoveryProviderBase<GitHubConnectionVerifier.GitHubConnectionContext>(logger)
{
    private const int PageSize = 100;

    /// <summary>
    ///     How many pages one repository is read across. A repository holding more open pull requests than
    ///     this, all touched since the last tick, is beyond what a scan can usefully answer anyway; the bound
    ///     exists so a host that ignores the page parameter is not an endless loop.
    /// </summary>
    private const int MaxPages = 20;

    /// <inheritdoc />
    public override ScmProvider Provider => ScmProvider.GitHub;

    /// <inheritdoc />
    protected override async Task<GitHubConnectionVerifier.GitHubConnectionContext> PrepareAsync(
        ActivePullRequestQuery query,
        CancellationToken ct)
    {
        return await connectionVerifier.VerifyAsync(query.ClientId, HostOf(query), ct);
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<ActivePullRequestRef>> ListRepositoryAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        ActivePullRequestQuery query,
        ClaimedRepositoryRef repository,
        CancellationToken ct)
    {
        var host = HostOf(query);
        var repositoryPath = await this.ResolveRepositoryPathAsync(context, host, repository, ct);
        var owner = repositoryPath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                    ?? string.Empty;
        var refs = new List<ActivePullRequestRef>();
        var reachedPageLimit = true;

        for (var page = 1; page <= MaxPages; page++)
        {
            var listingQuery = string.Create(
                CultureInfo.InvariantCulture,
                $"state=open&sort=updated&direction=desc&per_page={PageSize}&page={page}");

            using var request = await context.CreateAuthenticatedRequestAsync(
                GitHubConnectionVerifier.BuildApiUri(host, $"/repos/{repositoryPath}/pulls", listingQuery),
                ct: ct);
            using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);

            if (ProviderThrottleSignal.IsThrottled(response))
            {
                throw new ProviderThrottledException($"GitHub throttled the pull-request listing for {repositoryPath}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"GitHub pull-request listing for {repositoryPath} failed with status {(int)response.StatusCode}.");
            }

            var pullRequests =
                await response.Content.ReadFromJsonAsync<IReadOnlyList<GitHubPullRequestSummary>>(ct) ?? [];

            if (pullRequests.Count == 0)
            {
                reachedPageLimit = false;
                break;
            }

            var reachedWatermark = false;
            foreach (var pullRequest in pullRequests)
            {
                var updatedAt = pullRequest.UpdatedAt ?? pullRequest.CreatedAt;

                // Sorted newest first, so the first entry at or before the watermark ends the read: everything
                // after it is older still.
                if (updatedAt is null || updatedAt <= query.UpdatedAfter)
                {
                    reachedWatermark = true;
                    break;
                }

                refs.Add(
                    new ActivePullRequestRef(
                        query.ScopePath,
                        owner,

                        // The claimed identifier, so the scan matches what it reads back against the claim
                        // whether the configuration stored a numeric id or an owner/name path.
                        repository.RepositoryId,
                        pullRequest.Number,
                        updatedAt.Value));
            }

            if (reachedWatermark
                || ProviderPaginationHeaders.ReadGitHubHasMore(response) == false
                || pullRequests.Count < PageSize)
            {
                reachedPageLimit = false;
                break;
            }
        }

        if (reachedPageLimit)
        {
            ActivePullRequestDiscoveryLog.PageLimitReached(logger, this.Provider, repositoryPath, MaxPages);
        }

        return refs;
    }

    private static ProviderHostRef HostOf(ActivePullRequestQuery query)
    {
        return new ProviderHostRef(ScmProvider.GitHub, query.ScopePath);
    }

    /// <summary>
    ///     Turns what the configuration stored into the <c>owner/name</c> pair GitHub's API is addressed by.
    /// </summary>
    /// <remarks>
    ///     Guided selection stores the repository's numeric id, which this resolves to owner/name and which
    ///     survives a rename. A configuration holding a path instead, written by hand or by an earlier version,
    ///     is used as it stands and needs no request. Such a path does not follow a rename: the listing then
    ///     fails and the repository is logged as unreadable on every tick until it is selected again, which
    ///     stores the id.
    /// </remarks>
    private async Task<string> ResolveRepositoryPathAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        ProviderHostRef host,
        ClaimedRepositoryRef repository,
        CancellationToken ct)
    {
        if (LooksLikeRepositoryPath(repository.RepositoryId))
        {
            return repository.RepositoryId.Trim();
        }

        using var request = await context.CreateAuthenticatedRequestAsync(
            GitHubConnectionVerifier.BuildApiUri(
                host,
                $"/repositories/{Uri.EscapeDataString(repository.RepositoryId)}"),
            ct: ct);
        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);

        if (ProviderThrottleSignal.IsThrottled(response))
        {
            throw new ProviderThrottledException($"GitHub throttled the repository lookup for {repository.RepositoryId}.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub repository lookup for {repository.RepositoryId} failed with status {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<GitHubRepositorySummary>(ct);
        if (string.IsNullOrWhiteSpace(payload?.FullName))
        {
            throw new InvalidOperationException($"GitHub repository lookup for {repository.RepositoryId} returned no repository name.");
        }

        return payload.FullName.Trim();
    }

    private static bool LooksLikeRepositoryPath(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
               && value.Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 2
               && value.Contains('/', StringComparison.Ordinal);
    }

    private sealed record GitHubPullRequestSummary(
        [property: JsonPropertyName("number")] int Number,
        [property: JsonPropertyName("updated_at")]
        DateTimeOffset? UpdatedAt,
        [property: JsonPropertyName("created_at")]
        DateTimeOffset? CreatedAt);

    private sealed record GitHubRepositorySummary(
        [property: JsonPropertyName("full_name")]
        string? FullName);
}
