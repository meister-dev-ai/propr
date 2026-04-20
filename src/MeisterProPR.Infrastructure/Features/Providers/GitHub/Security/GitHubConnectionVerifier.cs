// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Providers.GitHub.Security;

internal sealed class GitHubConnectionVerifier(
    IClientScmConnectionRepository connectionRepository,
    IHttpClientFactory httpClientFactory)
{
    public async Task<GitHubConnectionContext> VerifyAsync(
        Guid clientId,
        ProviderHostRef host,
        CancellationToken ct = default)
    {
        EnsureGitHub(host);

        var connection = await connectionRepository.GetOperationalConnectionAsync(clientId, host, ct);
        if (connection is null)
        {
            throw new InvalidOperationException("No active GitHub connection is configured for the supplied host.");
        }

        if (connection.AuthenticationKind != ScmAuthenticationKind.PersonalAccessToken)
        {
            throw new InvalidOperationException("GitHub onboarding currently requires personal access token authentication.");
        }

        using var request = CreateAuthenticatedRequest(BuildApiUri(host, "/user"), connection.Secret);
        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("GitHub connection authentication failed.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitHub connection verification failed with status {(int)response.StatusCode}.");
        }

        var user = await response.Content.ReadFromJsonAsync<GitHubUserResponse>(ct);
        if (string.IsNullOrWhiteSpace(user?.Login))
        {
            throw new InvalidOperationException("GitHub connection verification did not return an authenticated user login.");
        }

        return new GitHubConnectionContext(connection, user.Login.Trim());
    }

    internal static HttpRequestMessage CreateAuthenticatedRequest(Uri uri, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    internal static Uri BuildApiUri(ProviderHostRef host, string relativePath, string? query = null)
    {
        EnsureGitHub(host);

        var normalizedPath = relativePath.StartsWith('/') ? relativePath : "/" + relativePath;
        var baseUrl = GetApiBaseUrl(host);
        var builder = new UriBuilder(baseUrl)
        {
            Path = normalizedPath,
            Query = query ?? string.Empty,
        };

        return builder.Uri;
    }

    internal static Uri BuildGraphQlUri(ProviderHostRef host)
    {
        EnsureGitHub(host);

        var uri = new Uri(host.HostBaseUrl);
        return string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            ? new Uri("https://api.github.com/graphql")
            : new Uri(host.HostBaseUrl.TrimEnd('/') + "/api/graphql");
    }

    private static string GetApiBaseUrl(ProviderHostRef host)
    {
        var uri = new Uri(host.HostBaseUrl);
        if (string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return "https://api.github.com";
        }

        return host.HostBaseUrl.TrimEnd('/') + "/api/v3";
    }

    private static void EnsureGitHub(ProviderHostRef host)
    {
        if (host.Provider != ScmProvider.GitHub)
        {
            throw new InvalidOperationException("This GitHub adapter only supports GitHub provider references.");
        }
    }

    internal sealed record GitHubConnectionContext(
        ClientScmConnectionCredentialDto Connection,
        string AuthenticatedLogin);

    private sealed record GitHubUserResponse([property: JsonPropertyName("login")] string? Login);
}
