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

namespace MeisterProPR.Infrastructure.Features.Providers.GitHub.Identity;

internal sealed class GitHubReviewerIdentityService(
    GitHubConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory) : IReviewerIdentityService
{
    public ScmProvider Provider => ScmProvider.GitHub;

    public async Task<IReadOnlyList<ReviewerIdentity>> ResolveCandidatesAsync(
        Guid clientId,
        ProviderHostRef host,
        string searchText,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchText);

        var context = await connectionVerifier.VerifyAsync(clientId, host, ct);
        return context.Connection.AuthenticationKind == ScmAuthenticationKind.AppInstallation
            ? await this.ResolveInstallationCandidatesAsync(context, host, searchText.Trim(), ct)
            : await this.ResolvePersonalAccessTokenCandidatesAsync(context, host, searchText.Trim(), ct);
    }

    private async Task<IReadOnlyList<ReviewerIdentity>> ResolvePersonalAccessTokenCandidatesAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        ProviderHostRef host,
        string searchText,
        CancellationToken ct)
    {
        var encodedQuery = Uri.EscapeDataString(searchText + " in:login");

        using var request = await context.CreateAuthenticatedRequestAsync(
            GitHubConnectionVerifier.BuildApiUri(host, "/search/users", $"q={encodedQuery}&per_page=20&type=Users"),
            ct: ct);
        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub reviewer identity lookup failed with status {(int)response.StatusCode}.");
        }

        var searchResponse = await response.Content.ReadFromJsonAsync<GitHubSearchResponse>(ct)
                             ?? new GitHubSearchResponse([]);
        var candidates = new Dictionary<string, ReviewerIdentity>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in searchResponse.Items)
        {
            if (string.IsNullOrWhiteSpace(item.Login))
            {
                continue;
            }

            var displayName = item.Login.Trim();
            var isBot = string.Equals(item.Type, "Bot", StringComparison.OrdinalIgnoreCase)
                        || displayName.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase);

            candidates[item.Id.ToString(CultureInfo.InvariantCulture)] = new ReviewerIdentity(
                host,
                item.Id.ToString(CultureInfo.InvariantCulture),
                displayName,
                displayName,
                isBot);
        }

        return candidates.Values.OrderBy(candidate => candidate.Login, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    private async Task<IReadOnlyList<ReviewerIdentity>> ResolveInstallationCandidatesAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        ProviderHostRef host,
        string searchText,
        CancellationToken ct)
    {
        var repositories = await this.ListInstallationRepositoriesAsync(context, host, ct);
        var candidates = new Dictionary<string, ReviewerIdentity>(StringComparer.OrdinalIgnoreCase);

        foreach (var repository in repositories)
        {
            if (string.IsNullOrWhiteSpace(repository.FullName))
            {
                continue;
            }

            foreach (var collaborator in await this.ListCollaboratorsAsync(context, host, repository.FullName.Trim(), ct))
            {
                if (string.IsNullOrWhiteSpace(collaborator.Login))
                {
                    continue;
                }

                var login = collaborator.Login.Trim();
                if (!login.Contains(searchText, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var externalUserId = collaborator.Id.ToString(CultureInfo.InvariantCulture);
                candidates[externalUserId] = new ReviewerIdentity(
                    host,
                    externalUserId,
                    login,
                    login,
                    IsBot(login, collaborator.Type));

                if (candidates.Count >= 20)
                {
                    return candidates.Values.OrderBy(candidate => candidate.Login, StringComparer.OrdinalIgnoreCase)
                        .ToList()
                        .AsReadOnly();
                }
            }
        }

        return candidates.Values.OrderBy(candidate => candidate.Login, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    private async Task<IReadOnlyList<GitHubInstallationRepositoryResponse>> ListInstallationRepositoriesAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        ProviderHostRef host,
        CancellationToken ct)
    {
        var repositories = new List<GitHubInstallationRepositoryResponse>();

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
                throw new InvalidOperationException(
                    $"GitHub reviewer identity lookup failed with status {(int)response.StatusCode}.");
            }

            var payload = await response.Content.ReadFromJsonAsync<GitHubInstallationRepositoriesResponse>(ct)
                          ?? throw new InvalidOperationException(
                              "GitHub reviewer identity lookup returned an empty installation-repository payload.");

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

    private async Task<IReadOnlyList<GitHubCollaboratorResponse>> ListCollaboratorsAsync(
        GitHubConnectionVerifier.GitHubConnectionContext context,
        ProviderHostRef host,
        string repositoryFullName,
        CancellationToken ct)
    {
        using var request = await context.CreateAuthenticatedRequestAsync(
            GitHubConnectionVerifier.BuildApiUri(
                host,
                $"/repos/{repositoryFullName}/collaborators",
                "per_page=100"),
            ct: ct);
        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GitHub reviewer identity lookup failed with status {(int)response.StatusCode}.");
        }

        return await response.Content.ReadFromJsonAsync<IReadOnlyList<GitHubCollaboratorResponse>>(ct) ?? [];
    }

    private static bool IsBot(string login, string? type)
    {
        return string.Equals(type, "Bot", StringComparison.OrdinalIgnoreCase)
               || login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record GitHubSearchResponse([property: JsonPropertyName("items")] IReadOnlyList<GitHubSearchUserResponse> Items);

    private sealed record GitHubInstallationRepositoriesResponse(
        [property: JsonPropertyName("repositories")]
        IReadOnlyList<GitHubInstallationRepositoryResponse> Repositories);

    private sealed record GitHubInstallationRepositoryResponse([property: JsonPropertyName("full_name")] string? FullName);

    private sealed record GitHubCollaboratorResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("login")] string? Login,
        [property: JsonPropertyName("type")] string? Type);

    private sealed record GitHubSearchUserResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("login")] string? Login,
        [property: JsonPropertyName("type")] string? Type);
}
