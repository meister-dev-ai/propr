// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Azure.Core;
using Azure.Identity;
using MeisterDev.Ai.Providers.Egress;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.OAuth;
using Microsoft.VisualStudio.Services.WebApi;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Security;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Support;

/// <summary>
///     Creates and caches <see cref="VssConnection" /> instances keyed by organisation URL and optional
///     per-client Azure DevOps connection credentials. OAuth connections are refreshed before the access token expires.
/// </summary>
/// <param name="credential">The installation credential used where a connection carries none of its own.</param>
/// <param name="egressUrlPolicy">
///     What this installation permits an operator-entered address to reach. Left out, the strictest posture
///     applies, so a caller that composes the factory itself cannot end up with a weaker one than the host has.
/// </param>
public sealed class VssConnectionFactory(TokenCredential credential, EgressUrlPolicy? egressUrlPolicy = null)
{
    private const string AdoResourceScope = "499b84ac-1321-427f-aa17-267ca6975798/.default";
    private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromMinutes(5);
    private static readonly DateTimeOffset NonExpiringCredentials = DateTimeOffset.MaxValue;

    private readonly ConcurrentDictionary<string, (VssConnection Connection, DateTimeOffset ExpiresOn)> _cache
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly EgressUrlPolicy _egressUrlPolicy = egressUrlPolicy ?? EgressUrlPolicy.Locked;

    /// <summary>
    ///     Returns a live <see cref="VssConnection" /> for the given organisation URL, acquiring or refreshing the token
    ///     as needed.
    /// </summary>
    /// <param name="organizationUrl">The Azure DevOps organisation URL (e.g. <c>https://dev.azure.com/myorg</c>).</param>
    /// <param name="credentials">Optional per-client Azure DevOps credentials; falls back to the global credential when <c>null</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<VssConnection> GetConnectionAsync(
        string organizationUrl,
        AdoConnectionCredentials? credentials = null,
        CancellationToken ct = default)
        => await this.GetConnectionCoreAsync(organizationUrl, credentials, false, ct);

    internal Task<VssConnection> GetOverviewConnectionAsync(string organizationUrl, AdoConnectionCredentials credentials, CancellationToken ct)
        => this.GetConnectionCoreAsync(organizationUrl, credentials, true, ct);

    private async Task<VssConnection> GetConnectionCoreAsync(string organizationUrl, AdoConnectionCredentials? credentials, bool overview, CancellationToken ct)
    {
        var normalizedUrl = organizationUrl.TrimEnd('/');
        this.RefuseUnlessPermitted(normalizedUrl);
        var cacheKey = BuildCacheKey(normalizedUrl, credentials) + (overview ? "::overview" : "");

        if (this._cache.TryGetValue(cacheKey, out var cached) &&
            cached.ExpiresOn - DateTimeOffset.UtcNow > ExpiryBuffer)
        {
            return cached.Connection;
        }

        var (conn, expiresOn) = await this.CreateConnectionAsync(normalizedUrl, credentials, ct, overview);

        this._cache[cacheKey] = (conn, expiresOn);
        return conn;
    }

    /// <summary>
    ///     Returns the HTTP <c>Authorization</c> header value suitable for git HTTPS operations, or <c>null</c>
    ///     when the authentication kind cannot be represented as an HTTP header (e.g. Windows/NTLM).
    /// </summary>
    /// <param name="organizationUrl">The Azure DevOps organisation URL the header is issued for.</param>
    /// <param name="credentials">Optional per-client Azure DevOps credentials; falls back to the global credential when <c>null</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    ///     The header carries the credential git sends with every request to that organisation, so the address
    ///     is held to the installation's rule before one is read or minted: a URL this installation refuses
    ///     never produces a credential to send anywhere.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     The installation refuses the organisation URL, which is a stored value an operator has to correct.
    /// </exception>
    public async Task<string?> GetHttpAuthorizationHeaderAsync(
        string organizationUrl,
        AdoConnectionCredentials? credentials,
        CancellationToken ct)
    {
        this.RefuseUnlessPermitted(organizationUrl.TrimEnd('/'));

        if (credentials?.AuthenticationKind == ScmAuthenticationKind.PersonalAccessToken)
        {
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($":{credentials.Secret}"));
            return $"AUTHORIZATION: Basic {payload}";
        }

        if (credentials?.AuthenticationKind == ScmAuthenticationKind.WindowsUserAccount)
        {
            return null;
        }

        // OAuthClientCredentials or global managed identity — acquire a Bearer token.
        var token = await this.GetRawTokenAsync(organizationUrl, credentials, ct);
        return $"AUTHORIZATION: Bearer {token}";
    }

    private async Task<string> GetRawTokenAsync(
        string organizationUrl,
        AdoConnectionCredentials? credentials,
        CancellationToken ct)
    {
        var normalizedUrl = organizationUrl.TrimEnd('/');

        if (credentials?.AuthenticationKind == ScmAuthenticationKind.OAuthClientCredentials)
        {
            if (string.IsNullOrWhiteSpace(credentials.OAuthTenantId) || string.IsNullOrWhiteSpace(credentials.OAuthClientId))
            {
                throw new InvalidOperationException("Azure DevOps OAuth credentials require OAuth tenant and client identifiers.");
            }

            var effectiveCredential = new ClientSecretCredential(
                credentials.OAuthTenantId,
                credentials.OAuthClientId,
                credentials.Secret);
            var token = await effectiveCredential.GetTokenAsync(new TokenRequestContext([AdoResourceScope]), ct);
            return token.Token;
        }

        // Global managed identity / default credential.
        _ = normalizedUrl;
        var globalToken = await credential.GetTokenAsync(new TokenRequestContext([AdoResourceScope]), ct);
        return globalToken.Token;
    }

    private async Task<(VssConnection Connection, DateTimeOffset ExpiresOn)> CreateConnectionAsync(
        string normalizedUrl,
        AdoConnectionCredentials? credentials,
        CancellationToken ct, bool overview = false)
    {
        if (credentials is null)
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext([AdoResourceScope]), ct);
            return (
                this.CreateGuardedConnection(normalizedUrl, new VssOAuthAccessTokenCredential(token.Token), overview: overview),
                token.ExpiresOn);
        }

        return credentials.AuthenticationKind switch
        {
            ScmAuthenticationKind.OAuthClientCredentials => await this.CreateOAuthConnectionAsync(normalizedUrl, credentials, ct, overview),
            ScmAuthenticationKind.PersonalAccessToken => (
                this.CreateGuardedConnection(normalizedUrl, new VssBasicCredential(string.Empty, credentials.Secret), overview: overview),
                NonExpiringCredentials),
            ScmAuthenticationKind.WindowsUserAccount => this.CreateWindowsConnection(normalizedUrl, credentials, overview),
            _ => throw new InvalidOperationException(
                $"Azure DevOps authentication kind '{credentials.AuthenticationKind}' is not supported by the runtime connection factory."),
        };
    }

    private async Task<(VssConnection Connection, DateTimeOffset ExpiresOn)> CreateOAuthConnectionAsync(
        string normalizedUrl,
        AdoConnectionCredentials credentials,
        CancellationToken ct, bool overview = false)
    {
        if (string.IsNullOrWhiteSpace(credentials.OAuthTenantId) || string.IsNullOrWhiteSpace(credentials.OAuthClientId))
        {
            throw new InvalidOperationException("Azure DevOps OAuth credentials require OAuth tenant and client identifiers.");
        }

        var effectiveCredential = new ClientSecretCredential(
            credentials.OAuthTenantId,
            credentials.OAuthClientId,
            credentials.Secret);
        var token = await effectiveCredential.GetTokenAsync(new TokenRequestContext([AdoResourceScope]), ct);
        return (
            this.CreateGuardedConnection(normalizedUrl, new VssOAuthAccessTokenCredential(token.Token), overview: overview),
            token.ExpiresOn);
    }

    /// <summary>
    ///     Builds a connection whose transport refuses a blocked egress address, so Azure DevOps traffic follows
    ///     the rule every other outbound call of this host follows.
    /// </summary>
    /// <param name="normalizedUrl">The organisation or collection URL, without a trailing slash.</param>
    /// <param name="credentials">What the connection authenticates with.</param>
    /// <param name="transportCredentials">
    ///     The account a Windows-authenticated connection answers a challenge with, or <see langword="null" />
    ///     where the credential rides on the request instead.
    /// </param>
    /// <remarks>
    ///     The SDK builds its own transport and applies its request settings to it, and it recognises only the
    ///     handler type it builds. A transport supplied from here therefore carries those settings itself: no
    ///     cookies, gzip where the settings enable compression, no automatic redirects, and the network
    ///     credential a Windows-authenticated connection needs the transport to answer a challenge with.
    /// </remarks>
    private VssConnection CreateGuardedConnection(
        string normalizedUrl,
        VssCredentials credentials,
        NetworkCredential? transportCredentials = null, bool overview = false)
    {
        var settings = VssClientHttpRequestSettings.Default.Clone();
        var transport = CreateGuardedTransport(
            this._egressUrlPolicy.AllowPrivateEgress,
            settings.CompressionEnabled,
            transportCredentials);

        return new VssConnection(
            new Uri(normalizedUrl),
            new VssHttpMessageHandler(credentials, settings, transport),
            overview ? [new AdoOverviewResponseHandler()] : []);
    }

    /// <summary>The transport every connection this factory hands out sends its requests through.</summary>
    /// <param name="allowPrivateEgress">Whether this installation permits a private, loopback or link-local address.</param>
    /// <param name="compressionEnabled">Whether the SDK's request settings ask for a compressed response.</param>
    /// <param name="transportCredentials">
    ///     The account a Windows-authenticated connection answers a challenge with, or <see langword="null" />
    ///     where the credential rides on the request instead.
    /// </param>
    /// <remarks>
    ///     A 3xx response is not followed. The rule is stated here and not read off the SDK's request settings,
    ///     so an SDK release that changes its own default leaves Azure DevOps traffic where every other
    ///     outbound call of this host is: a redirect reaches the caller as the response it is.
    /// </remarks>
    internal static SocketsHttpHandler CreateGuardedTransport(
        bool allowPrivateEgress,
        bool compressionEnabled,
        NetworkCredential? transportCredentials)
    {
        var transport = GuardedEgressHttpHandler.Create(allowPrivateEgress);
        transport.UseCookies = false;
        transport.AllowAutoRedirect = false;
        transport.AutomaticDecompression = compressionEnabled ? DecompressionMethods.GZip : DecompressionMethods.None;
        transport.Credentials = transportCredentials;

        return transport;
    }

    /// <summary>
    ///     Stops where this installation refuses <paramref name="normalizedUrl" /> as the address of a
    ///     source-control host.
    /// </summary>
    /// <param name="normalizedUrl">The organisation or collection URL, without a trailing slash.</param>
    /// <remarks>
    ///     Applied at the start of each entry point, before a credential is read, minted or asked of Azure AD.
    ///     A refused URL then costs no token and cannot surface as an authentication failure standing in for
    ///     the refusal. The scheme is decided here and not by the transport, which sees an address and never a
    ///     scheme: without this check a URL naming plain http would put the credential of every request on the
    ///     wire unencrypted.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    ///     The installation refuses the organisation URL, which is a stored value an operator has to correct.
    /// </exception>
    private void RefuseUnlessPermitted(string normalizedUrl)
    {
        var refusal = this._egressUrlPolicy.GetRepositoryHostRefusalReason(
            normalizedUrl,
            "The Azure DevOps organisation URL");
        if (refusal is not null)
        {
            throw new InvalidOperationException(refusal);
        }
    }

    private (VssConnection Connection, DateTimeOffset ExpiresOn) CreateWindowsConnection(
        string normalizedUrl,
        AdoConnectionCredentials credentials, bool overview = false)
    {
        var networkCredential = CreateWindowsNetworkCredential(credentials.UserName, credentials.Secret);

        return (
            this.CreateGuardedConnection(
                normalizedUrl,
                new VssCredentials(new WindowsCredential(networkCredential)),
                networkCredential, overview),
            NonExpiringCredentials);
    }

    internal static string BuildCacheKey(string normalizedUrl, AdoConnectionCredentials? credentials)
    {
        if (credentials is null)
        {
            return $"{normalizedUrl}::global";
        }

        return credentials.AuthenticationKind switch
        {
            ScmAuthenticationKind.OAuthClientCredentials =>
                $"{normalizedUrl}::oauth::{credentials.OAuthTenantId}::{credentials.OAuthClientId}::{ComputeCacheTokenFingerprint(credentials.Secret)}",
            ScmAuthenticationKind.PersonalAccessToken =>
                $"{normalizedUrl}::pat::{ComputeCacheTokenFingerprint(credentials.Secret)}",
            ScmAuthenticationKind.WindowsUserAccount =>
                $"{normalizedUrl}::windows::{credentials.UserName}::{ComputeCacheTokenFingerprint(credentials.Secret)}",
            _ => $"{normalizedUrl}::{credentials.AuthenticationKind}:{ComputeCacheTokenFingerprint(credentials.Secret)}",
        };
    }

    private static string ComputeCacheTokenFingerprint(string secret)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(bytes);
    }

    private static NetworkCredential CreateWindowsNetworkCredential(string? userName, string secret)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return new NetworkCredential(string.Empty, secret);
        }

        var normalizedUserName = userName.Trim();
        var separatorIndex = normalizedUserName.IndexOf('\\');
        if (separatorIndex > 0 && separatorIndex < normalizedUserName.Length - 1)
        {
            var domain = normalizedUserName[..separatorIndex];
            var account = normalizedUserName[(separatorIndex + 1)..];
            return new NetworkCredential(account, secret, domain);
        }

        return new NetworkCredential(normalizedUserName, secret);
    }
}
