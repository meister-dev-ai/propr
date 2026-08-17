// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Security;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Discovery;

internal sealed class GitLabDiscoveryService(
    GitLabConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory) : IRepositoryDiscoveryProvider
{
    public ScmProvider Provider => ScmProvider.GitLab;

    public async Task<IReadOnlyList<string>> ListScopesAsync(
        Guid clientId,
        ProviderHostRef host,
        CancellationToken ct = default)
    {
        var context = await connectionVerifier.VerifyAsync(clientId, host, ct);

        // Read across pages: an operator belonging to more groups than one page holds would otherwise be
        // offered a truncated list with nothing said about the rest.
        var groups = await ProviderRestPager.LoadAllAsync(
            (page, pageSize, pageCt) => this.LoadPageAsync<GitLabGroupResponse>(
                context,
                host,
                "/groups",
                page,
                pageSize,
                "scope",
                pageCt,
                "min_access_level=10"),
            group => group.FullPath ?? group.Path ?? string.Empty,
            "GitLab's group listing",
            ct);

        var scopePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            context.AuthenticatedUsername,
        };

        foreach (var group in groups)
        {
            var scopePath = string.IsNullOrWhiteSpace(group.FullPath)
                ? group.Path
                : group.FullPath;

            if (!string.IsNullOrWhiteSpace(scopePath))
            {
                scopePaths.Add(scopePath.Trim());
            }
        }

        return scopePaths.OrderBy(scope => scope, StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly();
    }

    public async Task<IReadOnlyList<RepositoryRef>> ListRepositoriesAsync(
        Guid clientId,
        ProviderHostRef host,
        string scopePath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopePath);

        var normalizedScopePath = scopePath.Trim();
        var context = await connectionVerifier.VerifyAsync(clientId, host, ct);
        var isPersonalScope = string.Equals(
            normalizedScopePath,
            context.AuthenticatedUsername,
            StringComparison.OrdinalIgnoreCase);

        var path = isPersonalScope
            ? "/projects"
            : $"/groups/{Uri.EscapeDataString(normalizedScopePath)}/projects";
        var filter = isPersonalScope
            ? "owned=true&membership=true&simple=true"
            : "include_subgroups=true&simple=true";

        // A group with more projects than one page holds is read to the end, nested subgroups included. A
        // truncated list would offer only part of the group's projects.
        var repositories = await ProviderRestPager.LoadAllAsync(
            (page, pageSize, pageCt) => this.LoadPageAsync<GitLabProjectResponse>(
                context,
                host,
                path,
                page,
                pageSize,
                "repository",
                pageCt,
                filter),
            project => project.Id.ToString(CultureInfo.InvariantCulture),
            $"GitLab's project listing for {normalizedScopePath}",
            ct);

        return repositories
            .Where(project => !string.IsNullOrWhiteSpace(project.PathWithNamespace))
            .Select(project => ToRepository(host, project))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>Reads one page of a GitLab listing, following the host's own answer about the next page.</summary>
    /// <remarks>
    ///     A 404 is a scope the connection cannot see, which is an empty result rather than a failure: an
    ///     operator who picked a group they have no access to is told by the empty list, where a fault would
    ///     suggest the connection itself is broken.
    /// </remarks>
    private async Task<ProviderRestPager.RestPage<T>> LoadPageAsync<T>(
        GitLabConnectionVerifier.GitLabConnectionContext context,
        ProviderHostRef host,
        string path,
        int page,
        int pageSize,
        string collectionKind,
        CancellationToken ct,
        string? filter = null)
    {
        var pageQuery = string.Create(CultureInfo.InvariantCulture, $"per_page={pageSize}&page={page}");
        var query = string.IsNullOrEmpty(filter) ? pageQuery : $"{pageQuery}&{filter}";

        using var request = GitLabConnectionVerifier.CreateAuthenticatedRequest(
            GitLabConnectionVerifier.BuildApiUri(host, path, query),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("GitLabProvider").SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ProviderRestPager.RestPage<T>([], HasMore: false);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitLab {collectionKind} discovery failed with status {(int)response.StatusCode}.");
        }

        var items = await response.Content.ReadFromJsonAsync<IReadOnlyList<T>>(ct) ?? [];
        return new ProviderRestPager.RestPage<T>(items, ProviderPaginationHeaders.ReadGitLabHasMore(response));
    }

    private static RepositoryRef ToRepository(ProviderHostRef host, GitLabProjectResponse project)
    {
        var projectPath = project.PathWithNamespace!.Trim();
        var ownerOrNamespace = project.Namespace?.FullPath;
        if (string.IsNullOrWhiteSpace(ownerOrNamespace))
        {
            var separatorIndex = projectPath.LastIndexOf('/');
            ownerOrNamespace = separatorIndex > 0
                ? projectPath[..separatorIndex]
                : projectPath;
        }

        return new RepositoryRef(
            host,
            project.Id.ToString(CultureInfo.InvariantCulture),
            ownerOrNamespace,
            projectPath);
    }

    private sealed record GitLabGroupResponse(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("full_path")]
        string? FullPath);

    private sealed record GitLabProjectResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("path_with_namespace")]
        string? PathWithNamespace,
        [property: JsonPropertyName("namespace")]
        GitLabNamespaceResponse? Namespace);

    private sealed record GitLabNamespaceResponse(
        [property: JsonPropertyName("full_path")]
        string? FullPath);
}
