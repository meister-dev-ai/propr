// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Providers.GitLab.Security;

internal sealed class GitLabConnectionVerifier(
    IClientScmConnectionRepository connectionRepository,
    IHttpClientFactory httpClientFactory)
{
    public async Task<GitLabConnectionContext> VerifyAsync(
        Guid clientId,
        ProviderHostRef host,
        CancellationToken ct = default)
    {
        EnsureGitLab(host);

        var connection = await connectionRepository.GetOperationalConnectionAsync(clientId, host, ct);
        if (connection is null)
        {
            throw new InvalidOperationException("No active GitLab connection is configured for the supplied host.");
        }

        if (connection.AuthenticationKind != ScmAuthenticationKind.PersonalAccessToken)
        {
            throw new InvalidOperationException("GitLab onboarding currently requires personal access token authentication.");
        }

        using var request = CreateAuthenticatedRequest(BuildApiUri(host, "/user"), connection.Secret);
        using var response = await httpClientFactory.CreateClient("GitLabProvider").SendAsync(request, ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("GitLab connection authentication failed.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GitLab connection verification failed with status {(int)response.StatusCode}.");
        }

        var user = await response.Content.ReadFromJsonAsync<GitLabUserResponse>(ct);
        if (string.IsNullOrWhiteSpace(user?.Username))
        {
            throw new InvalidOperationException("GitLab connection verification did not return an authenticated username.");
        }

        return new GitLabConnectionContext(connection, user.Username.Trim());
    }

    internal static HttpRequestMessage CreateAuthenticatedRequest(Uri uri, string token, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, uri);
        request.Headers.Add("PRIVATE-TOKEN", token);
        return request;
    }

    internal static Uri BuildApiUri(ProviderHostRef host, string relativePath, string? query = null)
    {
        EnsureGitLab(host);

        var normalizedPath = relativePath.StartsWith('/') ? relativePath : "/" + relativePath;
        var builder = new UriBuilder(host.HostBaseUrl)
        {
            Path = $"/api/v4{normalizedPath}",
            Query = query ?? string.Empty,
        };

        return builder.Uri;
    }

    private static void EnsureGitLab(ProviderHostRef host)
    {
        if (host.Provider != ScmProvider.GitLab)
        {
            throw new InvalidOperationException("This GitLab adapter only supports GitLab provider references.");
        }
    }

    internal sealed record GitLabConnectionContext(
        ClientScmConnectionCredentialDto Connection,
        string AuthenticatedUsername);

    private sealed record GitLabUserResponse(
        [property: JsonPropertyName("username")]
        string? Username);
}
