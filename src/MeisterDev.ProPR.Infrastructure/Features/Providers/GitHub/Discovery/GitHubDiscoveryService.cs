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
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Discovery;

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

        if (context.Connection.AuthenticationKind == ScmAuthenticationKind.AppInstallation)
        {
            var installationRepositories = await this.ListInstallationRepositoriesAsync(context, host, ct);
            var installationScopePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var repository in installationRepositories)
            {
                if (!string.IsNullOrWhiteSpace(repository.Owner?.Login))
                {
                    installationScopePaths.Add(repository.Owner.Login.Trim());
                }
            }

            if (!string.IsNullOrWhiteSpace(context.AuthenticatedLogin))
            {
                installationScopePaths.Add(context.AuthenticatedLogin);
            }

            return installationScopePaths.OrderBy(scope => scope, StringComparer.OrdinalIgnoreCase)
                .ToList()
                .AsReadOnly();
        }

        // Read across pages: an operator whose account belongs to more organizations than one page holds
        // would otherwise be offered a truncated list with nothing said about the rest.
        var organizations = await ProviderRestPager.LoadAllAsync(
            (page, pageSize, pageCt) => this.LoadPageAsync<GitHubOrganizationResponse>(
                context,
                host,
                "/user/orgs",
                page,
                pageSize,
                "organization",
                pageCt),
            organization => organization.Login ?? string.Empty,
            "GitHub's organization listing",
            ct);

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

        if (context.Connection.AuthenticationKind == ScmAuthenticationKind.AppInstallation)
        {
            var installationRepositories = await this.ListInstallationRepositoriesAsync(context, host, ct);
            return installationRepositories
                .Where(repository => !string.IsNullOrWhiteSpace(repository.FullName)
                                     && !string.IsNullOrWhiteSpace(repository.Owner?.Login)
                                     && string.Equals(
                                         repository.Owner.Login,
                                         normalizedScopePath,
                                         StringComparison.OrdinalIgnoreCase))
                .Select(repository => new RepositoryRef(
                    host,
                    repository.Id.ToString(CultureInfo.InvariantCulture),
                    repository.Owner!.Login!.Trim(),
                    repository.FullName!.Trim()))
                .ToList()
                .AsReadOnly();
        }

        var isPersonalScope = string.Equals(
            normalizedScopePath,
            context.AuthenticatedLogin,
            StringComparison.OrdinalIgnoreCase);
        var path = isPersonalScope
            ? "/user/repos"
            : $"/orgs/{Uri.EscapeDataString(normalizedScopePath)}/repos";
        var filter = isPersonalScope
            ? "affiliation=owner,collaborator,organization_member"
            : "type=all";

        // Read to the end for an owner with more repositories than one page holds. A truncated list would
        // offer only part of the owner's repositories, with nothing to say the rest exist.
        var discoveredRepositories = await ProviderRestPager.LoadAllAsync(
            (page, pageSize, pageCt) => this.LoadPageAsync<GitHubRepositoryResponse>(
                context,
                host,
                path,
                page,
                pageSize,
                "repository",
                pageCt,
                filter),
            repository => repository.Id.ToString(CultureInfo.InvariantCulture),
            $"GitHub's repository listing for {normalizedScopePath}",
            ct);

        return discoveredRepositories
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

    /// <summary>Reads one page of a GitHub listing, following its own answer about whether more remains.</summary>
    /// <remarks>
    ///     A repository listing answers 404 for a scope the connection cannot see, which is an empty result
    ///     rather than a failure: an operator who picked an owner they have no access to is told by the empty
    ///     list, where a fault would suggest the connection itself is broken.
    /// </remarks>
    private async Task<ProviderRestPager.RestPage<T>> LoadPageAsync<T>(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        ProviderHostRef host,
        string path,
        int page,
        int pageSize,
        string collectionKind,
        CancellationToken ct,
        string? filter = null)
    {
        var pageQuery = string.Create(
            CultureInfo.InvariantCulture,
            $"per_page={pageSize}&page={page}");
        var query = string.IsNullOrEmpty(filter) ? pageQuery : $"{pageQuery}&{filter}";

        using var request = await context.CreateAuthenticatedRequestAsync(
            GitHubConnectionVerifier.BuildApiUri(host, path, query),
            ct: ct);
        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new ProviderRestPager.RestPage<T>([], HasMore: false);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub {collectionKind} discovery failed with status {(int)response.StatusCode}.");
        }

        var items = await response.Content.ReadFromJsonAsync<IReadOnlyList<T>>(ct) ?? [];
        return new ProviderRestPager.RestPage<T>(items, ProviderPaginationHeaders.ReadGitHubHasMore(response));
    }

    private async Task<IReadOnlyList<GitHubRepositoryResponse>> ListInstallationRepositoriesAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        ProviderHostRef host,
        CancellationToken ct)
    {
        var repositories = new List<GitHubRepositoryResponse>();

        for (var page = 1; page <= 10; page++)
        {
            using var request = await context.CreateAuthenticatedRequestAsync(
                GitHubConnectionVerifier.BuildApiUri(
                    host,
                    "/installation/repositories",
                    $"per_page=100&page={page.ToString(CultureInfo.InvariantCulture)}"),
                ct: ct);
            using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"GitHub installation repository discovery failed with status {(int)response.StatusCode}.");
            }

            var payload = await response.Content.ReadFromJsonAsync<GitHubInstallationRepositoriesResponse>(ct)
                          ?? throw new InvalidOperationException("GitHub installation repository discovery returned an empty payload.");

            if (payload.Repositories.Count == 0)
            {
                break;
            }

            repositories.AddRange(payload.Repositories);
            if (payload.Repositories.Count < 100)
            {
                break;
            }
        }

        return repositories.AsReadOnly();
    }

    private sealed record GitHubInstallationRepositoriesResponse(
        [property: JsonPropertyName("repositories")]
        IReadOnlyList<GitHubRepositoryResponse> Repositories);

    private sealed record GitHubOrganizationResponse([property: JsonPropertyName("login")] string? Login);

    private sealed record GitHubRepositoryResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("full_name")]
        string? FullName,
        [property: JsonPropertyName("owner")] GitHubOwnerResponse? Owner);

    private sealed record GitHubOwnerResponse([property: JsonPropertyName("login")] string? Login);
}
