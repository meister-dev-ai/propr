// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.Forgejo.Security;

namespace MeisterProPR.Infrastructure.Features.Providers.Forgejo.Discovery;

internal sealed class ForgejoDiscoveryService(
    ForgejoConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory) : IRepositoryDiscoveryProvider
{
    public ScmProvider Provider => ScmProvider.Forgejo;

    public async Task<IReadOnlyList<string>> ListScopesAsync(
        Guid clientId,
        ProviderHostRef host,
        CancellationToken ct = default)
    {
        var context = await connectionVerifier.VerifyAsync(clientId, host, ct);

        using var request = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
            ForgejoConnectionVerifier.BuildApiUri(host, "/user/orgs", "limit=100"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("ForgejoProvider").SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Forgejo scope discovery failed with status {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<IReadOnlyList<ForgejoOrganizationResponse>>(ct)
                      ?? [];
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            context.AuthenticatedUsername,
        };

        foreach (var organization in payload)
        {
            var scope = organization.Username;
            if (!string.IsNullOrWhiteSpace(scope))
            {
                scopes.Add(scope.Trim());
            }
        }

        return scopes.OrderBy(scope => scope, StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly();
    }

    public async Task<IReadOnlyList<RepositoryRef>> ListRepositoriesAsync(
        Guid clientId,
        ProviderHostRef host,
        string scopePath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopePath);

        var normalizedScope = scopePath.Trim();
        var context = await connectionVerifier.VerifyAsync(clientId, host, ct);
        var isPersonalScope = string.Equals(
            normalizedScope,
            context.AuthenticatedUsername,
            StringComparison.OrdinalIgnoreCase);

        var path = isPersonalScope
            ? "/user/repos"
            : $"/orgs/{Uri.EscapeDataString(normalizedScope)}/repos";

        using var request = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
            ForgejoConnectionVerifier.BuildApiUri(host, path, "limit=100"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("ForgejoProvider").SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Forgejo repository discovery failed with status {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<IReadOnlyList<ForgejoRepositoryResponse>>(ct)
                      ?? [];

        return payload
            .Where(repository => !string.IsNullOrWhiteSpace(repository.FullName))
            .Select(repository => ToRepository(host, repository))
            .ToList()
            .AsReadOnly();
    }

    private static RepositoryRef ToRepository(ProviderHostRef host, ForgejoRepositoryResponse repository)
    {
        var projectPath = repository.FullName!.Trim();
        var owner = repository.Owner?.Login;
        if (string.IsNullOrWhiteSpace(owner))
        {
            owner = projectPath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(owner))
        {
            throw new InvalidOperationException("Forgejo repository discovery did not return an owner or namespace.");
        }

        return new RepositoryRef(
            host,
            repository.Id.ToString(CultureInfo.InvariantCulture),
            owner,
            projectPath);
    }

    private sealed record ForgejoOrganizationResponse(
        [property: JsonPropertyName("username")]
        string? Username);

    private sealed record ForgejoRepositoryResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("full_name")]
        string? FullName,
        [property: JsonPropertyName("owner")] ForgejoOwnerResponse? Owner);

    private sealed record ForgejoOwnerResponse([property: JsonPropertyName("login")] string? Login);
}
