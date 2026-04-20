// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Providers.Forgejo.Security;

internal sealed class ForgejoConnectionVerifier(
    IClientScmConnectionRepository connectionRepository,
    IHttpClientFactory httpClientFactory)
{
    public async Task<ForgejoConnectionContext> VerifyAsync(
        Guid clientId,
        ProviderHostRef host,
        CancellationToken ct = default)
    {
        EnsureForgejo(host);

        var connection = await connectionRepository.GetOperationalConnectionAsync(clientId, host, ct);
        if (connection is null)
        {
            throw new InvalidOperationException("No active Forgejo connection is configured for the supplied host.");
        }

        if (connection.AuthenticationKind != ScmAuthenticationKind.PersonalAccessToken)
        {
            throw new InvalidOperationException("Forgejo onboarding currently requires personal access token authentication.");
        }

        using var request = CreateAuthenticatedRequest(BuildApiUri(host, "/user"), connection.Secret);
        using var response = await httpClientFactory.CreateClient("ForgejoProvider").SendAsync(request, ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException("Forgejo connection authentication failed.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Forgejo connection verification failed with status {(int)response.StatusCode}.");
        }

        var user = await response.Content.ReadFromJsonAsync<ForgejoUserResponse>(ct);
        if (string.IsNullOrWhiteSpace(user?.Login))
        {
            throw new InvalidOperationException("Forgejo connection verification did not return an authenticated username.");
        }

        return new ForgejoConnectionContext(connection, user.Login.Trim());
    }

    internal static HttpRequestMessage CreateAuthenticatedRequest(Uri uri, string token, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Authorization", $"token {token}");
        request.Headers.Accept.ParseAdd("application/json");
        return request;
    }

    internal static Uri BuildApiUri(ProviderHostRef host, string relativePath, string? query = null)
    {
        EnsureForgejo(host);

        var normalizedPath = relativePath.StartsWith('/') ? relativePath : "/" + relativePath;
        var baseUri = new Uri(host.HostBaseUrl);
        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        var builder = new UriBuilder(baseUri)
        {
            Path = $"{basePath}/api/v1{normalizedPath}",
            Query = query ?? string.Empty,
        };

        return builder.Uri;
    }

    private static void EnsureForgejo(ProviderHostRef host)
    {
        if (host.Provider != ScmProvider.Forgejo)
        {
            throw new InvalidOperationException("This Forgejo adapter only supports Forgejo provider references.");
        }
    }

    internal sealed record ForgejoConnectionContext(
        ClientScmConnectionCredentialDto Connection,
        string AuthenticatedUsername);

    private sealed record ForgejoUserResponse([property: JsonPropertyName("login")] string? Login);
}
