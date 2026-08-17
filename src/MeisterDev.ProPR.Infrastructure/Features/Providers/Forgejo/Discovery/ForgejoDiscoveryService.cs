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
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Security;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Discovery;

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

        // Read across pages: an operator belonging to more organizations than one page holds would otherwise
        // be offered a truncated list with nothing said about the rest.
        var payload = await ProviderRestPager.LoadAllAsync(
            (page, pageSize, pageCt) => this.LoadPageAsync<ForgejoOrganizationResponse>(
                context,
                host,
                "/user/orgs",
                page,
                pageSize,
                "scope",
                pageCt),
            organization => organization.Username ?? string.Empty,
            "Forgejo's organization listing",
            ct);

        // The authenticated account is an owner in its own right, alongside the organizations it belongs to.
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

        // An owner with more repositories than one page holds is read to the end. Forgejo also clamps a
        // requested page size to the host's own maximum, so how much remains comes from its total rather than
        // from counting what came back.
        var payload = await ProviderRestPager.LoadAllAsync(
            (page, pageSize, pageCt) => this.LoadPageAsync<ForgejoRepositoryResponse>(
                context,
                host,
                path,
                page,
                pageSize,
                "repository",
                pageCt),
            repository => repository.Id.ToString(CultureInfo.InvariantCulture),
            $"Forgejo's repository listing for {normalizedScope}",
            ct);

        return payload
            .Where(repository => !string.IsNullOrWhiteSpace(repository.FullName))
            .Select(repository => ToRepository(host, repository))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>Reads one page of a Forgejo listing, following the total the host reports.</summary>
    /// <remarks>
    ///     A 404 is a scope the connection cannot see, which is an empty result rather than a failure: an
    ///     operator who picked an owner they have no access to is told by the empty list, where a fault would
    ///     suggest the connection itself is broken.
    /// </remarks>
    private async Task<ProviderRestPager.RestPage<T>> LoadPageAsync<T>(
        ForgejoConnectionVerifier.ForgejoConnectionContext context,
        ProviderHostRef host,
        string path,
        int page,
        int pageSize,
        string collectionKind,
        CancellationToken ct)
    {
        // The first page asks for a size and no page number, which is the request a single-page collection
        // made before this read was paginated.
        var size = string.Create(CultureInfo.InvariantCulture, $"limit={pageSize}");
        var query = page <= 1 ? size : string.Create(CultureInfo.InvariantCulture, $"page={page}&{size}");

        using var request = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
            ForgejoConnectionVerifier.BuildApiUri(host, path, query),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("ForgejoProvider").SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ProviderRestPager.RestPage<T>([], HasMore: false);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Forgejo {collectionKind} discovery failed with status {(int)response.StatusCode}.");
        }

        var items = await response.Content.ReadFromJsonAsync<IReadOnlyList<T>>(ct) ?? [];
        return new ProviderRestPager.RestPage<T>(
            items,
            TotalCount: ProviderPaginationHeaders.ReadForgejoTotalCount(response));
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
