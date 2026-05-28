// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Azure.Core;
using Azure.Identity;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Enums;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.OAuth;
using Microsoft.VisualStudio.Services.WebApi;

namespace MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.Support;

/// <summary>
///     Creates and caches <see cref="VssConnection" /> instances keyed by organisation URL and optional
///     per-client Azure DevOps connection credentials. OAuth connections are refreshed before the access token expires.
/// </summary>
public sealed class VssConnectionFactory(TokenCredential credential)
{
    private const string AdoResourceScope = "499b84ac-1321-427f-aa17-267ca6975798/.default";
    private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromMinutes(5);
    private static readonly DateTimeOffset NonExpiringCredentials = DateTimeOffset.MaxValue;

    private readonly ConcurrentDictionary<string, (VssConnection Connection, DateTimeOffset ExpiresOn)> _cache
        = new(StringComparer.OrdinalIgnoreCase);

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
    {
        var normalizedUrl = organizationUrl.TrimEnd('/');
        var cacheKey = BuildCacheKey(normalizedUrl, credentials);

        if (this._cache.TryGetValue(cacheKey, out var cached) &&
            cached.ExpiresOn - DateTimeOffset.UtcNow > ExpiryBuffer)
        {
            return cached.Connection;
        }

        var (conn, expiresOn) = await this.CreateConnectionAsync(normalizedUrl, credentials, ct);

        this._cache[cacheKey] = (conn, expiresOn);
        return conn;
    }

    private async Task<(VssConnection Connection, DateTimeOffset ExpiresOn)> CreateConnectionAsync(
        string normalizedUrl,
        AdoConnectionCredentials? credentials,
        CancellationToken ct)
    {
        if (credentials is null)
        {
            var token = await credential.GetTokenAsync(new TokenRequestContext([AdoResourceScope]), ct);
            return (
                new VssConnection(
                    new Uri(normalizedUrl),
                    new VssOAuthAccessTokenCredential(token.Token)),
                token.ExpiresOn);
        }

        return credentials.AuthenticationKind switch
        {
            ScmAuthenticationKind.OAuthClientCredentials => await this.CreateOAuthConnectionAsync(normalizedUrl, credentials, ct),
            ScmAuthenticationKind.PersonalAccessToken => (
                new VssConnection(new Uri(normalizedUrl), new VssBasicCredential(string.Empty, credentials.Secret)),
                NonExpiringCredentials),
            ScmAuthenticationKind.WindowsUserAccount => (
                new VssConnection(
                    new Uri(normalizedUrl),
                    new VssCredentials(new WindowsCredential(CreateWindowsNetworkCredential(credentials.UserName, credentials.Secret)))),
                NonExpiringCredentials),
            _ => throw new InvalidOperationException(
                $"Azure DevOps authentication kind '{credentials.AuthenticationKind}' is not supported by the runtime connection factory."),
        };
    }

    private async Task<(VssConnection Connection, DateTimeOffset ExpiresOn)> CreateOAuthConnectionAsync(
        string normalizedUrl,
        AdoConnectionCredentials credentials,
        CancellationToken ct)
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
            new VssConnection(
                new Uri(normalizedUrl),
                new VssOAuthAccessTokenCredential(token.Token)),
            token.ExpiresOn);
    }

    private static string BuildCacheKey(string normalizedUrl, AdoConnectionCredentials? credentials)
    {
        if (credentials is null)
        {
            return $"{normalizedUrl}::global";
        }

        return credentials.AuthenticationKind switch
        {
            ScmAuthenticationKind.OAuthClientCredentials =>
                $"{normalizedUrl}::oauth::{credentials.OAuthClientId}",
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
