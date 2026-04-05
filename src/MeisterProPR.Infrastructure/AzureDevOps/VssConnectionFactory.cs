// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using Azure.Core;
using Azure.Identity;
using MeisterProPR.Application.DTOs;
using Microsoft.VisualStudio.Services.OAuth;
using Microsoft.VisualStudio.Services.WebApi;

namespace MeisterProPR.Infrastructure.AzureDevOps;

/// <summary>
///     Creates and caches <see cref="VssConnection" /> instances keyed by organisation URL and optional
///     per-client service-principal credentials. Connections are refreshed before the access token expires.
/// </summary>
public sealed class VssConnectionFactory(TokenCredential credential)
{
    private const string AdoResourceScope = "499b84ac-1321-427f-aa17-267ca6975798/.default";
    private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (VssConnection Connection, DateTimeOffset ExpiresOn)> _cache
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Returns a live <see cref="VssConnection" /> for the given organisation URL, acquiring or refreshing the token
    ///     as needed.
    /// </summary>
    /// <param name="organizationUrl">The Azure DevOps organisation URL (e.g. <c>https://dev.azure.com/myorg</c>).</param>
    /// <param name="credentials">
    ///     Optional per-client service-principal credentials; falls back to the global credential when
    ///     <c>null</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<VssConnection> GetConnectionAsync(
        string organizationUrl,
        ClientAdoCredentials? credentials = null,
        CancellationToken ct = default)
    {
        var normalizedUrl = organizationUrl.TrimEnd('/');
        var cacheKey = $"{normalizedUrl}::{credentials?.ClientId ?? "global"}";

        if (this._cache.TryGetValue(cacheKey, out var cached) &&
            cached.ExpiresOn - DateTimeOffset.UtcNow > ExpiryBuffer)
        {
            return cached.Connection;
        }

        var effectiveCredential = credentials is not null
            ? new ClientSecretCredential(credentials.TenantId, credentials.ClientId, credentials.Secret)
            : credential;

        var token = await effectiveCredential.GetTokenAsync(new TokenRequestContext([AdoResourceScope]), ct);
        var conn = new VssConnection(
            new Uri(normalizedUrl),
            new VssOAuthAccessTokenCredential(token.Token));

        this._cache[cacheKey] = (conn, token.ExpiresOn);
        return conn;
    }
}
