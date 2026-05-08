// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Runtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace MeisterProPR.Infrastructure.Features.Providers.GitHub.Security;

internal sealed class GitHubAuthenticationService
{
    private static readonly JwtSecurityTokenHandler TokenHandler = new();
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GitHubInstallationTokenCache _installationTokenCache;
    private readonly ILogger<GitHubAuthenticationService> _logger;

    public GitHubAuthenticationService(
        IHttpClientFactory httpClientFactory,
        GitHubInstallationTokenCache? installationTokenCache = null,
        ILogger<GitHubAuthenticationService>? logger = null)
    {
        this._httpClientFactory = httpClientFactory;
        this._installationTokenCache = installationTokenCache ?? new GitHubInstallationTokenCache();
        this._logger = logger ?? NullLogger<GitHubAuthenticationService>.Instance;
    }

    public Task<string> GetAccessTokenAsync(
        ProviderHostRef host,
        ClientScmConnectionCredentialDto connection,
        CancellationToken ct = default)
    {
        return connection.AuthenticationKind switch
        {
            ScmAuthenticationKind.PersonalAccessToken => Task.FromResult(connection.Secret),
            ScmAuthenticationKind.AppInstallation => this.GetInstallationAccessTokenAsync(host, connection, ct),
            _ => Task.FromException<string>(
                new InvalidOperationException("GitHub connection authentication kind is not supported.")),
        };
    }

    public async Task<GitHubInstallationMetadata> GetInstallationMetadataAsync(
        ProviderHostRef host,
        ClientScmConnectionCredentialDto connection,
        CancellationToken ct = default)
    {
        ValidateAppInstallationConnection(connection);

        var installationId = connection.GitHubAppInstallationId!.Value.ToString(CultureInfo.InvariantCulture);
        this._logger.LogDebug(
            "Looking up GitHub App installation {InstallationId} for connection {ConnectionId} on host {HostBaseUrl}.",
            installationId,
            connection.Id,
            host.HostBaseUrl);
        var appJwt = CreateAppJwt(connection);
        using var request = GitHubConnectionVerifier.CreateAuthenticatedRequest(
            GitHubConnectionVerifier.BuildApiUri(host, $"/app/installations/{installationId}"),
            appJwt);
        using var response = await this._httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var detail = await ReadResponseMessageAsync(response, ct);
            this._logger.LogWarning(
                "GitHub App installation lookup failed for connection {ConnectionId} on host {HostBaseUrl} with status {StatusCode}. Detail: {Detail}",
                connection.Id,
                host.HostBaseUrl,
                (int)response.StatusCode,
                detail);
            throw new InvalidOperationException(
                BuildMessage(
                    "GitHub App authentication failed.",
                    detail));
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            this._logger.LogWarning(
                "GitHub App installation {InstallationId} was not found for connection {ConnectionId} on host {HostBaseUrl}.",
                installationId,
                connection.Id,
                host.HostBaseUrl);
            throw new InvalidOperationException("GitHub App installation was not found or is not accessible.");
        }

        if (!response.IsSuccessStatusCode)
        {
            this._logger.LogWarning(
                "GitHub App installation lookup failed for connection {ConnectionId} on host {HostBaseUrl} with status {StatusCode}.",
                connection.Id,
                host.HostBaseUrl,
                (int)response.StatusCode);
            throw new InvalidOperationException(
                await BuildStatusMessageAsync("GitHub App installation lookup failed", response, ct));
        }

        var payload = await response.Content.ReadFromJsonAsync<GitHubInstallationResponse>(ct)
                      ?? throw new InvalidOperationException(
                          "GitHub App installation lookup returned an empty payload.");

        if (string.IsNullOrWhiteSpace(payload.Account?.Login))
        {
            throw new InvalidOperationException(
                "GitHub App installation lookup did not return an installation account login.");
        }

        this._logger.LogDebug(
            "GitHub App installation {InstallationId} resolved for connection {ConnectionId} as account {AccountLogin}.",
            installationId,
            connection.Id,
            payload.Account.Login.Trim());

        return new GitHubInstallationMetadata(
            payload.Account.Login.Trim(),
            payload.AppSlug?.Trim());
    }

    public async Task<GitHubAppMetadata> GetAppMetadataAsync(
        ProviderHostRef host,
        ClientScmConnectionCredentialDto connection,
        CancellationToken ct = default)
    {
        ValidateAppInstallationConnection(connection);

        using var request = GitHubConnectionVerifier.CreateAuthenticatedRequest(
            GitHubConnectionVerifier.BuildApiUri(host, "/app"),
            CreateAppJwt(connection));
        using var response = await this._httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                await BuildStatusMessageAsync("GitHub App metadata lookup failed", response, ct));
        }

        var payload = await response.Content.ReadFromJsonAsync<GitHubAppResponse>(ct)
                      ?? throw new InvalidOperationException("GitHub App metadata lookup returned an empty payload.");

        if (string.IsNullOrWhiteSpace(payload.Slug))
        {
            throw new InvalidOperationException("GitHub App metadata lookup did not return an app slug.");
        }

        return new GitHubAppMetadata(
            payload.Slug.Trim(),
            string.IsNullOrWhiteSpace(payload.Name) ? payload.Slug.Trim() : payload.Name.Trim());
    }

    private async Task<string> GetInstallationAccessTokenAsync(
        ProviderHostRef host,
        ClientScmConnectionCredentialDto connection,
        CancellationToken ct)
    {
        ValidateAppInstallationConnection(connection);

        var cacheKey = BuildCacheKey(host, connection);
        if (this._installationTokenCache.TryGet(cacheKey, out var cachedToken))
        {
            return cachedToken;
        }

        var installationId = connection.GitHubAppInstallationId!.Value.ToString(CultureInfo.InvariantCulture);
        this._logger.LogDebug(
            "Minting GitHub App installation token for installation {InstallationId} on connection {ConnectionId}.",
            installationId,
            connection.Id);
        var appJwt = CreateAppJwt(connection);
        using var request = GitHubConnectionVerifier.CreateAuthenticatedRequest(
            GitHubConnectionVerifier.BuildApiUri(host, $"/app/installations/{installationId}/access_tokens"),
            appJwt,
            HttpMethod.Post);
        using var response = await this._httpClientFactory.CreateClient("GitHubProvider").SendAsync(request, ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            var detail = await ReadResponseMessageAsync(response, ct);
            this._logger.LogWarning(
                "GitHub App installation token request failed for connection {ConnectionId} on host {HostBaseUrl} with status {StatusCode}. Detail: {Detail}",
                connection.Id,
                host.HostBaseUrl,
                (int)response.StatusCode,
                detail);
            throw new InvalidOperationException(
                BuildMessage(
                    "GitHub App installation token request failed.",
                    detail));
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            this._logger.LogWarning(
                "GitHub App installation token request could not find installation {InstallationId} for connection {ConnectionId}.",
                installationId,
                connection.Id);
            throw new InvalidOperationException("GitHub App installation was not found or is not accessible.");
        }

        if (!response.IsSuccessStatusCode)
        {
            this._logger.LogWarning(
                "GitHub App installation token request failed for connection {ConnectionId} on host {HostBaseUrl} with status {StatusCode}.",
                connection.Id,
                host.HostBaseUrl,
                (int)response.StatusCode);
            throw new InvalidOperationException(
                await BuildStatusMessageAsync("GitHub App installation token request failed", response, ct));
        }

        var payload = await response.Content.ReadFromJsonAsync<GitHubInstallationAccessTokenResponse>(ct)
                      ?? throw new InvalidOperationException(
                          "GitHub App installation token request returned an empty payload.");

        if (string.IsNullOrWhiteSpace(payload.Token) || payload.ExpiresAt is null)
        {
            throw new InvalidOperationException(
                "GitHub App installation token request did not return a valid access token.");
        }

        this._installationTokenCache.Set(cacheKey, payload.Token.Trim(), payload.ExpiresAt.Value);
        this._logger.LogDebug(
            "Cached GitHub App installation token for connection {ConnectionId} until {ExpiresAt}.",
            connection.Id,
            payload.ExpiresAt.Value);
        return payload.Token.Trim();
    }

    private static string BuildCacheKey(ProviderHostRef host, ClientScmConnectionCredentialDto connection)
    {
        var secretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(connection.Secret)));
        return string.Join(
            '|',
            host.HostBaseUrl,
            connection.Id.ToString("N", CultureInfo.InvariantCulture),
            connection.GitHubAppId!.Value.ToString(CultureInfo.InvariantCulture),
            connection.GitHubAppInstallationId!.Value.ToString(CultureInfo.InvariantCulture),
            secretHash);
    }

    private static void ValidateAppInstallationConnection(ClientScmConnectionCredentialDto connection)
    {
        if (connection.AuthenticationKind != ScmAuthenticationKind.AppInstallation)
        {
            throw new InvalidOperationException("GitHub App authentication requires app installation credentials.");
        }

        if (!connection.GitHubAppId.HasValue || connection.GitHubAppId.Value <= 0)
        {
            throw new InvalidOperationException("GitHub App ID is missing from the saved provider connection.");
        }

        if (!connection.GitHubAppInstallationId.HasValue || connection.GitHubAppInstallationId.Value <= 0)
        {
            throw new InvalidOperationException("GitHub App installation ID is missing from the saved provider connection.");
        }

        if (string.IsNullOrWhiteSpace(connection.Secret))
        {
            throw new InvalidOperationException("GitHub App private key is missing from the saved provider connection.");
        }
    }

    private static string CreateAppJwt(ClientScmConnectionCredentialDto connection)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(connection.Secret.AsSpan());

            var now = DateTimeOffset.UtcNow;
            var issuedAt = now.AddSeconds(-60);
            var expiresAt = now.AddMinutes(9);
            var signingKey = new RsaSecurityKey(rsa.ExportParameters(true))
            {
                CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false },
            };
            var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256);
            var header = new JwtHeader(credentials);
            var payload = new JwtPayload
            {
                { JwtRegisteredClaimNames.Iat, issuedAt.ToUnixTimeSeconds() },
                { JwtRegisteredClaimNames.Exp, expiresAt.ToUnixTimeSeconds() },
                { JwtRegisteredClaimNames.Iss, connection.GitHubAppId!.Value.ToString(CultureInfo.InvariantCulture) },
            };

            return TokenHandler.WriteToken(new JwtSecurityToken(header, payload));
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            throw new InvalidOperationException("GitHub App private key is invalid.", ex);
        }
    }

    private static async Task<string> BuildStatusMessageAsync(
        string prefix,
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var detail = await ReadResponseMessageAsync(response, ct);
        return string.IsNullOrWhiteSpace(detail)
            ? $"{prefix} with status {(int)response.StatusCode}."
            : $"{prefix} with status {(int)response.StatusCode}: {detail}";
    }

    private static string BuildMessage(string message, string? detail)
    {
        return string.IsNullOrWhiteSpace(detail) ? message : $"{message} {detail}";
    }

    private static async Task<string?> ReadResponseMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("message", out var messageElement)
                && messageElement.ValueKind == JsonValueKind.String)
            {
                return messageElement.GetString()?.Trim();
            }
        }
        catch (JsonException)
        {
        }

        var normalized = body.Trim();
        return normalized.Length <= 512 ? normalized : normalized[..512].TrimEnd() + "...";
    }

    internal sealed record GitHubInstallationMetadata(string AccountLogin, string? AppSlug);

    internal sealed record GitHubAppMetadata(string Slug, string DisplayName);

    private sealed record GitHubInstallationResponse(
        [property: JsonPropertyName("account")]
        GitHubInstallationAccountResponse? Account,
        [property: JsonPropertyName("app_slug")]
        string? AppSlug);

    private sealed record GitHubAppResponse(
        [property: JsonPropertyName("slug")]
        string? Slug,
        [property: JsonPropertyName("name")]
        string? Name);

    private sealed record GitHubInstallationAccountResponse([property: JsonPropertyName("login")] string? Login);

    private sealed record GitHubInstallationAccessTokenResponse(
        [property: JsonPropertyName("token")]
        string? Token,
        [property: JsonPropertyName("expires_at")]
        DateTimeOffset? ExpiresAt);
}
