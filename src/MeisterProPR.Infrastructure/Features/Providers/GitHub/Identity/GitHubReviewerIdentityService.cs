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
        var encodedQuery = Uri.EscapeDataString(searchText.Trim() + " in:login");

        using var request = GitHubConnectionVerifier.CreateAuthenticatedRequest(
            GitHubConnectionVerifier.BuildApiUri(host, "/search/users", $"q={encodedQuery}&per_page=20&type=Users"),
            context.Connection.Secret);
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

    private sealed record GitHubSearchResponse([property: JsonPropertyName("items")] IReadOnlyList<GitHubSearchUserResponse> Items);

    private sealed record GitHubSearchUserResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("login")] string? Login,
        [property: JsonPropertyName("type")] string? Type);
}
