// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Security;

namespace MeisterProPR.Infrastructure.Features.Providers.GitHub.Discovery;

internal sealed class GitHubDiscoveryService(
    GitHubConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory) : IRepositoryDiscoveryProvider
{
    public ScmProvider Provider => ScmProvider.GitHub;

    public async Task<IReadOnlyList<string>> ListScopesAsync(
        Guid clientId,
        ProviderHostRef host,
        CancellationToken ct = default)
    {
        var context = await connectionVerifier.VerifyAsync(clientId, host, ct);

        using var request = GitHubConnectionVerifier.CreateAuthenticatedRequest(
            GitHubConnectionVerifier.BuildApiUri(host, "/user/orgs", "per_page=100"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub scope discovery failed with status {(int)response.StatusCode}.");
        }

        var organizations = await response.Content.ReadFromJsonAsync<IReadOnlyList<GitHubOrganizationResponse>>(ct)
                            ?? [];

        var scopePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            context.AuthenticatedLogin,
        };

        foreach (var organization in organizations)
        {
            if (!string.IsNullOrWhiteSpace(organization.Login))
            {
                scopePaths.Add(organization.Login.Trim());
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

        var path = string.Equals(normalizedScopePath, context.AuthenticatedLogin, StringComparison.OrdinalIgnoreCase)
            ? "/user/repos"
            : $"/orgs/{Uri.EscapeDataString(normalizedScopePath)}/repos";
        var query = string.Equals(normalizedScopePath, context.AuthenticatedLogin, StringComparison.OrdinalIgnoreCase)
            ? "per_page=100&affiliation=owner,collaborator,organization_member"
            : "per_page=100&type=all";

        using var request = GitHubConnectionVerifier.CreateAuthenticatedRequest(
            GitHubConnectionVerifier.BuildApiUri(host, path, query),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub repository discovery failed with status {(int)response.StatusCode}.");
        }

        var repositories = await response.Content.ReadFromJsonAsync<IReadOnlyList<GitHubRepositoryResponse>>(ct)
                           ?? [];

        return repositories
            .Where(repository => !string.IsNullOrWhiteSpace(repository.FullName) &&
                                 !string.IsNullOrWhiteSpace(repository.Owner?.Login))
            .Select(repository => new RepositoryRef(
                host,
                repository.Id.ToString(CultureInfo.InvariantCulture),
                repository.Owner!.Login!.Trim(),
                repository.FullName!.Trim()))
            .ToList()
            .AsReadOnly();
    }

    private sealed record GitHubOrganizationResponse([property: JsonPropertyName("login")] string? Login);

    private sealed record GitHubRepositoryResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("full_name")]
        string? FullName,
        [property: JsonPropertyName("owner")] GitHubOwnerResponse? Owner);

    private sealed record GitHubOwnerResponse([property: JsonPropertyName("login")] string? Login);
}
