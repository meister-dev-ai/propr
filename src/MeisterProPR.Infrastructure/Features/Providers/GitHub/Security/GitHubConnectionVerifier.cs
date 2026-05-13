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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterProPR.Infrastructure.Features.Providers.GitHub.Security;

internal sealed class GitHubConnectionVerifier(
    IClientScmConnectionRepository connectionRepository,
    IHttpClientFactory httpClientFactory,
    GitHubAuthenticationService? authenticationService = null,
    ILogger<GitHubConnectionVerifier>? logger = null)
{
    private readonly GitHubAuthenticationService _authenticationService = authenticationService
                                                                          ?? new GitHubAuthenticationService(httpClientFactory);

    private readonly ILogger<GitHubConnectionVerifier> _logger = logger ?? NullLogger<GitHubConnectionVerifier>.Instance;

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

        return connection.AuthenticationKind switch
        {
            ScmAuthenticationKind.PersonalAccessToken => await this.VerifyPersonalAccessTokenAsync(connection, host, ct),
            ScmAuthenticationKind.AppInstallation => await this.VerifyAppInstallationAsync(connection, host, ct),
            _ => throw new InvalidOperationException("GitHub connection authentication kind is not supported."),
        };
    }

    private async Task<GitHubConnectionContext> VerifyPersonalAccessTokenAsync(
        ClientScmConnectionCredentialDto connection,
        ProviderHostRef host,
        CancellationToken ct)
    {
        using var request = CreateAuthenticatedRequest(
            BuildApiUri(host, "/user"),
            await this._authenticationService.GetAccessTokenAsync(host, connection, ct));
        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);
        var safeHostBaseUrl = host.HostBaseUrl.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            this._logger.LogWarning(
                "GitHub PAT verification failed for connection {ConnectionId} on host {HostBaseUrl} with status {StatusCode}.",
                connection.Id,
                safeHostBaseUrl,
                (int)response.StatusCode);
            throw new InvalidOperationException("GitHub connection authentication failed.");
        }

        if (!response.IsSuccessStatusCode)
        {
            this._logger.LogWarning(
                "GitHub PAT verification failed for connection {ConnectionId} on host {HostBaseUrl} with status {StatusCode}.",
                connection.Id,
                safeHostBaseUrl,
                (int)response.StatusCode);
            throw new InvalidOperationException($"GitHub connection verification failed with status {(int)response.StatusCode}.");
        }

        var user = await response.Content.ReadFromJsonAsync<GitHubUserResponse>(ct);
        if (string.IsNullOrWhiteSpace(user?.Login))
        {
            throw new InvalidOperationException("GitHub connection verification did not return an authenticated user login.");
        }

        this._logger.LogDebug(
            "GitHub PAT verification succeeded for connection {ConnectionId} on host {HostBaseUrl} as {AuthenticatedLogin}.",
            connection.Id,
            safeHostBaseUrl,
            user.Login.Trim().Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t"));
        return new GitHubConnectionContext(
            connection,
            user.Login.Trim(),
            user.Login.Trim(),
            host,
            this._authenticationService);
    }

    private async Task<GitHubConnectionContext> VerifyAppInstallationAsync(
        ClientScmConnectionCredentialDto connection,
        ProviderHostRef host,
        CancellationToken ct)
    {
        var installation = await this._authenticationService.GetInstallationMetadataAsync(host, connection, ct);
        _ = await this._authenticationService.GetAccessTokenAsync(host, connection, ct);
        var safeHostBaseUrl = host.HostBaseUrl.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        this._logger.LogDebug(
            "GitHub App verification succeeded for connection {ConnectionId} on host {HostBaseUrl} as installation account {AuthenticatedLogin}.",
            connection.Id,
            safeHostBaseUrl,
            installation.AccountLogin.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t"));
        var authenticatedActorLogin = string.IsNullOrWhiteSpace(installation.AppSlug)
            ? installation.AccountLogin
            : installation.AppSlug.Trim() + "[bot]";
        return new GitHubConnectionContext(
            connection,
            installation.AccountLogin,
            authenticatedActorLogin,
            host,
            this._authenticationService);
    }

    internal static HttpRequestMessage CreateAuthenticatedRequest(Uri uri, string token, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, uri);
        AuthorizeRequest(request, token);
        return request;
    }

    internal static void AuthorizeRequest(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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

    internal sealed class GitHubConnectionContext
    {
        private readonly GitHubAuthenticationService _authenticationService;
        private readonly ProviderHostRef _host;

        internal GitHubConnectionContext(
            ClientScmConnectionCredentialDto connection,
            string authenticatedLogin,
            string authenticatedActorLogin,
            ProviderHostRef host,
            GitHubAuthenticationService authenticationService)
        {
            this.Connection = connection;
            this.AuthenticatedLogin = authenticatedLogin;
            this.AuthenticatedActorLogin = authenticatedActorLogin;
            this._host = host;
            this._authenticationService = authenticationService;
        }

        public ClientScmConnectionCredentialDto Connection { get; }

        public string AuthenticatedLogin { get; }

        public string AuthenticatedActorLogin { get; }

        public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
        {
            return await this._authenticationService.GetAccessTokenAsync(this._host, this.Connection, ct);
        }

        public async Task<HttpRequestMessage> CreateAuthenticatedRequestAsync(
            Uri uri,
            HttpMethod? method = null,
            CancellationToken ct = default)
        {
            var request = new HttpRequestMessage(method ?? HttpMethod.Get, uri);
            await this.AuthorizeRequestAsync(request, ct);
            return request;
        }

        public async Task AuthorizeRequestAsync(HttpRequestMessage request, CancellationToken ct = default)
        {
            AuthorizeRequest(request, await this.GetAccessTokenAsync(ct));
        }
    }

    private sealed record GitHubUserResponse([property: JsonPropertyName("login")] string? Login);
}
