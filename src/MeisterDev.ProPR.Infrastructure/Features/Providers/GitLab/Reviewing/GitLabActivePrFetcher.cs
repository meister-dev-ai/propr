// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Security;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Reviewing;

/// <summary>
///     GitLab implementation of <see cref="IActivePullRequestDiscoveryProvider" />.
/// </summary>
/// <remarks>
///     GitLab calls the unit a merge request, which the product models as a pull request everywhere else.
///     Its listing filters on an updated-after timestamp, so the watermark is applied by the host and the read
///     costs one request per project in the ordinary case.
///     A draft merge request is open, so it is answerable like any other. The host comes from the client's
///     connection, so a self-managed instance is reached at its own address.
/// </remarks>
internal sealed class GitLabActivePrFetcher(
    GitLabConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory,
    ILogger<GitLabActivePrFetcher> logger)
    : ActivePullRequestDiscoveryProviderBase<GitLabConnectionVerifier.GitLabConnectionContext>(logger)
{
    private const int PageSize = 100;

    /// <summary>
    ///     How many pages one project is read across. Far past what a project touched since the last tick
    ///     holds; the bound exists so a host that ignores the page parameter is not an endless loop.
    /// </summary>
    private const int MaxPages = 20;

    /// <inheritdoc />
    public override ScmProvider Provider => ScmProvider.GitLab;

    /// <inheritdoc />
    protected override async Task<GitLabConnectionVerifier.GitLabConnectionContext> PrepareAsync(
        ActivePullRequestQuery query,
        CancellationToken ct)
    {
        return await connectionVerifier.VerifyAsync(query.ClientId, HostOf(query), ct);
    }

    /// <inheritdoc />
    protected override async Task<IReadOnlyList<ActivePullRequestRef>> ListRepositoryAsync(
        GitLabConnectionVerifier.GitLabConnectionContext context,
        ActivePullRequestQuery query,
        ClaimedRepositoryRef repository,
        CancellationToken ct)
    {
        var host = HostOf(query);

        // A project in a nested subgroup is addressed by its full path, which must reach GitLab as a single
        // path segment. Without escaping, group/subgroup/project would be read as a path to a group.
        var projectId = Uri.EscapeDataString(repository.RepositoryId);
        var refs = new List<ActivePullRequestRef>();
        var reachedPageLimit = true;

        for (var page = 1; page <= MaxPages; page++)
        {
            var listingQuery = string.Create(
                CultureInfo.InvariantCulture,
                $"state=opened&order_by=updated_at&sort=desc&updated_after={Uri.EscapeDataString(query.UpdatedAfter.UtcDateTime.ToString("o", CultureInfo.InvariantCulture))}&per_page={PageSize}&page={page}");

            using var request = GitLabConnectionVerifier.CreateAuthenticatedRequest(
                GitLabConnectionVerifier.BuildApiUri(host, $"/projects/{projectId}/merge_requests", listingQuery),
                context.Connection.Secret);
            using var response = await httpClientFactory.CreateClient("GitLabProvider").SendAsync(request, ct);

            if (ProviderThrottleSignal.IsThrottled(response))
            {
                throw new ProviderThrottledException($"GitLab throttled the merge-request listing for {repository.RepositoryId}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"GitLab merge-request listing for {repository.RepositoryId} failed with status {(int)response.StatusCode}.");
            }

            var mergeRequests =
                await response.Content.ReadFromJsonAsync<IReadOnlyList<GitLabMergeRequestSummary>>(ct) ?? [];

            foreach (var mergeRequest in mergeRequests)
            {
                refs.Add(
                    new ActivePullRequestRef(
                        query.ScopePath,
                        NamespaceOf(mergeRequest.References?.Full ?? repository.DisplayName ?? repository.RepositoryId),

                        // The claimed identifier, so the scan matches what it reads back against the claim
                        // whether the configuration stored a numeric id or a project path.
                        repository.RepositoryId,

                        // The project-scoped iid, which is how every other GitLab call addresses a merge
                        // request. The global id would address a different merge request.
                        mergeRequest.Iid,
                        mergeRequest.UpdatedAt ?? mergeRequest.CreatedAt ?? query.UpdatedAfter));
            }

            if (mergeRequests.Count == 0
                || ProviderPaginationHeaders.ReadGitLabHasMore(response) == false
                || mergeRequests.Count < PageSize)
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

    private static ProviderHostRef HostOf(ActivePullRequestQuery query)
    {
        return new ProviderHostRef(ScmProvider.GitLab, query.ScopePath);
    }

    /// <summary>Takes the group path out of a full reference such as <c>group/subgroup/project!42</c>.</summary>
    private static string NamespaceOf(string reference)
    {
        var projectPath = reference.Split('!', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                          ?? reference;
        var separatorIndex = projectPath.LastIndexOf('/');
        return separatorIndex > 0 ? projectPath[..separatorIndex] : projectPath;
    }

    private sealed record GitLabMergeRequestSummary(
        [property: JsonPropertyName("iid")] int Iid,
        [property: JsonPropertyName("updated_at")]
        DateTimeOffset? UpdatedAt,
        [property: JsonPropertyName("created_at")]
        DateTimeOffset? CreatedAt,
        [property: JsonPropertyName("references")]
        GitLabMergeRequestReferences? References);

    private sealed record GitLabMergeRequestReferences([property: JsonPropertyName("full")] string? Full);
}
