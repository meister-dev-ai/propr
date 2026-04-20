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

namespace MeisterProPR.Infrastructure.Features.Providers.GitLab.Identity;

internal sealed class GitLabReviewerIdentityService(
    GitLabConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory) : IReviewerIdentityService
{
    public ScmProvider Provider => ScmProvider.GitLab;

    public async Task<IReadOnlyList<ReviewerIdentity>> ResolveCandidatesAsync(
        Guid clientId,
        ProviderHostRef host,
        string searchText,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchText);

        var context = await connectionVerifier.VerifyAsync(clientId, host, ct);
        var encodedQuery = Uri.EscapeDataString(searchText.Trim());

        using var request = GitLabConnectionVerifier.CreateAuthenticatedRequest(
            GitLabConnectionVerifier.BuildApiUri(host, "/users", $"search={encodedQuery}&per_page=20"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("GitLabProvider").SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitLab reviewer identity lookup failed with status {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<IReadOnlyList<GitLabUserResponse>>(ct)
                      ?? [];
        var candidates = new Dictionary<string, ReviewerIdentity>(StringComparer.OrdinalIgnoreCase);

        foreach (var user in payload)
        {
            if (string.IsNullOrWhiteSpace(user.Username))
            {
                continue;
            }

            var externalUserId = user.Id.ToString(CultureInfo.InvariantCulture);
            var login = user.Username.Trim();
            var displayName = string.IsNullOrWhiteSpace(user.Name)
                ? login
                : user.Name.Trim();

            candidates[externalUserId] = new ReviewerIdentity(
                host,
                externalUserId,
                login,
                displayName,
                user.Bot);
        }

        return candidates.Values.OrderBy(candidate => candidate.Login, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    private sealed record GitLabUserResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("username")]
        string? Username,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("bot")] bool Bot);
}
