// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Security;

namespace MeisterProPR.Infrastructure.Features.Providers.GitLab.Discovery;

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

        using var request = GitLabConnectionVerifier.CreateAuthenticatedRequest(
            GitLabConnectionVerifier.BuildApiUri(host, "/groups", "per_page=100&min_access_level=10"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("GitLabProvider").SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitLab scope discovery failed with status {(int)response.StatusCode}.");
        }

        var groups = await response.Content.ReadFromJsonAsync<IReadOnlyList<GitLabGroupResponse>>(ct)
                     ?? [];

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
        var query = isPersonalScope
            ? "owned=true&membership=true&simple=true&per_page=100"
            : "include_subgroups=true&simple=true&per_page=100";

        using var request = GitLabConnectionVerifier.CreateAuthenticatedRequest(
            GitLabConnectionVerifier.BuildApiUri(host, path, query),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("GitLabProvider").SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitLab repository discovery failed with status {(int)response.StatusCode}.");
        }

        var repositories = await response.Content.ReadFromJsonAsync<IReadOnlyList<GitLabProjectResponse>>(ct)
                           ?? [];

        return repositories
            .Where(project => !string.IsNullOrWhiteSpace(project.PathWithNamespace))
            .Select(project => ToRepository(host, project))
            .ToList()
            .AsReadOnly();
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
