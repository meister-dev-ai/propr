// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Support;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;

internal sealed class GitHubConnectionVerifier(
    IClientScmConnectionRepository connectionRepository,
    IHttpClientFactory httpClientFactory,
    GitHubAuthenticationService? authenticationService = null,
    ILogger<GitHubConnectionVerifier>? logger = null)
{
    private readonly GitHubAuthenticationService _authenticationService = authenticationService
                                                                          ?? new GitHubAuthenticationService(httpClientFactory);

    private readonly ILogger<GitHubConnectionVerifier> _logger = logger ?? NullLogger<GitHubConnectionVerifier>.Instance;

    public Task<GitHubAuthenticationService.GitHubAppMetadata> GetAppMetadataAsync(
        ProviderHostRef host, ClientScmConnectionCredentialDto connection, CancellationToken ct = default)
        => this._authenticationService.GetAppMetadataAsync(host, connection, ct);

    public async Task<GitHubConnectionContext> VerifyAsync(ConnectionDiscoveryContext context, CancellationToken ct = default)
    {
        EnsureGitHub(context.Host);
        var connection = await connectionRepository.GetOperationalConnectionByIdAsync(context.ClientId, context.ConnectionId, ct).ConfigureAwait(false);
        if (connection is null || !connection.IsActive || connection.Id != context.ConnectionId || connection.ClientId != context.ClientId ||
            connection.ProviderFamily != context.Host.Provider ||
            !string.Equals(
                new ProviderHostRef(connection.ProviderFamily, connection.HostBaseUrl).HostBaseUrl,
                context.Host.HostBaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The selected connection is not available for this client and host.");
        }

        return await this.VerifyConnectionAsync(connection, context.Host, ct).ConfigureAwait(false);
    }

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

        return await this.VerifyConnectionAsync(connection, host, ct).ConfigureAwait(false);
    }

    public async Task<GitHubConnectionContext> VerifyAsync(Guid clientId, ProviderHostRef host, ReviewDiscoveryContext context, CancellationToken ct = default)
    {
        EnsureGitHub(host);
        var connection = await ManualReviewDiscoveryCredentials.ResolveAsync(
            connectionRepository, clientId, host, context, new GitHubReviewSourcePolicy().IsSelectedScopeCompatible, ct).ConfigureAwait(false);
        return await this.VerifyConnectionAsync(connection, host, ct, true).ConfigureAwait(false);
    }

    private async Task<GitHubConnectionContext> VerifyConnectionAsync(
        ClientScmConnectionCredentialDto connection, ProviderHostRef host, CancellationToken ct, bool readOutcomes = false)
    {
        return connection.AuthenticationKind switch
        {
            ScmAuthenticationKind.PersonalAccessToken => await this.VerifyPersonalAccessTokenAsync(connection, host, ct, readOutcomes),
            ScmAuthenticationKind.AppInstallation => await this.VerifyAppInstallationAsync(connection, host, ct, readOutcomes),
            _ => throw new InvalidOperationException("GitHub connection authentication kind is not supported."),
        };
    }

    private async Task<GitHubConnectionContext> VerifyPersonalAccessTokenAsync(
        ClientScmConnectionCredentialDto connection,
        ProviderHostRef host,
        CancellationToken ct, bool readOutcomes)
    {
        using var request = CreateAuthenticatedRequest(
            BuildApiUri(host, "/user"),
            await this._authenticationService.GetAccessTokenAsync(host, connection, ct));
        using var response = await httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);
        var safeHostBaseUrl = host.HostBaseUrl.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

        if (readOutcomes)
        {
            GitHubReadFailures.ThrowIfDeniedOrThrottled(response, true);
        }

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
            this._authenticationService, readOutcomes);
    }

    private async Task<GitHubConnectionContext> VerifyAppInstallationAsync(
        ClientScmConnectionCredentialDto connection,
        ProviderHostRef host,
        CancellationToken ct, bool readOutcomes)
    {
        var installation = await this._authenticationService.GetInstallationMetadataAsync(host, connection, ct, readOutcomes);
        _ = await this._authenticationService.GetAccessTokenAsync(host, connection, ct, readOutcomes);
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
            this._authenticationService, readOutcomes);
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
        var baseUri = new Uri(GetApiBaseUrl(host));

        // Appended to the base path rather than replacing it. GitHub Enterprise Server serves its API under
        // /api/v3, and assigning the path outright dropped that prefix, so every REST call to an enterprise
        // host went to the web address of the resource instead of its API.
        var builder = new UriBuilder(baseUri)
        {
            Path = baseUri.AbsolutePath.TrimEnd('/') + normalizedPath,
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
        private readonly bool _readOutcomes;

        internal GitHubConnectionContext(
            ClientScmConnectionCredentialDto connection,
            string authenticatedLogin,
            string authenticatedActorLogin,
            ProviderHostRef host,
            GitHubAuthenticationService authenticationService, bool readOutcomes = false)
        {
            this.Connection = connection;
            this.AuthenticatedLogin = authenticatedLogin;
            this.AuthenticatedActorLogin = authenticatedActorLogin;
            this._host = host;
            this._authenticationService = authenticationService;
            this._readOutcomes = readOutcomes;
        }

        public ClientScmConnectionCredentialDto Connection { get; }

        public string AuthenticatedLogin { get; }

        public string AuthenticatedActorLogin { get; }

        public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
        {
            return await this._authenticationService.GetAccessTokenAsync(this._host, this.Connection, ct, this._readOutcomes);
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
