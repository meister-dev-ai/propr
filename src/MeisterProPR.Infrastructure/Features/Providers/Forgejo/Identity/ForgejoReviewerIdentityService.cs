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

namespace MeisterProPR.Infrastructure.Features.Providers.Forgejo.Identity;

internal sealed class ForgejoReviewerIdentityService(
    ForgejoConnectionVerifier connectionVerifier,
    IHttpClientFactory httpClientFactory) : IReviewerIdentityService
{
    public ScmProvider Provider => ScmProvider.Forgejo;

    public async Task<IReadOnlyList<ReviewerIdentity>> ResolveCandidatesAsync(
        Guid clientId,
        ProviderHostRef host,
        string searchText,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(searchText);

        var context = await connectionVerifier.VerifyAsync(clientId, host, ct);
        var encodedQuery = Uri.EscapeDataString(searchText.Trim());

        using var request = ForgejoConnectionVerifier.CreateAuthenticatedRequest(
            ForgejoConnectionVerifier.BuildApiUri(host, "/users/search", $"q={encodedQuery}&limit=20"),
            context.Connection.Secret);
        using var response = await httpClientFactory.CreateClient("ForgejoProvider").SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Forgejo reviewer identity lookup failed with status {(int)response.StatusCode}.");
        }

        var payload = await response.Content.ReadFromJsonAsync<ForgejoUserSearchResponse>(ct);
        var candidates = new Dictionary<string, ReviewerIdentity>(StringComparer.OrdinalIgnoreCase);

        foreach (var user in payload?.Data ?? [])
        {
            if (string.IsNullOrWhiteSpace(user.Login))
            {
                continue;
            }

            var externalUserId = user.Id.ToString(CultureInfo.InvariantCulture);
            var login = user.Login.Trim();
            var displayName = string.IsNullOrWhiteSpace(user.FullName)
                ? login
                : user.FullName.Trim();

            candidates[externalUserId] = new ReviewerIdentity(
                host,
                externalUserId,
                login,
                displayName,
                LooksLikeBot(login));
        }

        return candidates.Values.OrderBy(candidate => candidate.Login, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }

    private static bool LooksLikeBot(string login)
    {
        return login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase)
               || login.EndsWith("-bot", StringComparison.OrdinalIgnoreCase)
               || login.EndsWith("_bot", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ForgejoUserSearchResponse([property: JsonPropertyName("data")] IReadOnlyList<ForgejoUserResponse>? Data);

    private sealed record ForgejoUserResponse(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("login")] string? Login,
        [property: JsonPropertyName("full_name")]
        string? FullName);
}
