using System.Collections.Concurrent;
using Azure.Core;
using Azure.Identity;
using MeisterProPR.Application.DTOs;
using Microsoft.VisualStudio.Services.OAuth;
using Microsoft.VisualStudio.Services.WebApi;

namespace MeisterProPR.Infrastructure.AzureDevOps;

public sealed class VssConnectionFactory(TokenCredential credential)
{
    private const string AdoResourceScope = "499b84ac-1321-427f-aa17-267ca6975798/.default";
    private static readonly TimeSpan ExpiryBuffer = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (VssConnection Connection, DateTimeOffset ExpiresOn)> _cache
        = new(StringComparer.OrdinalIgnoreCase);

    public async Task<VssConnection> GetConnectionAsync(
        string organizationUrl,
        ClientAdoCredentials? credentials = null,
        CancellationToken ct = default)
    {
        var cacheKey = $"{organizationUrl}::{credentials?.ClientId ?? "global"}";

        if (_cache.TryGetValue(cacheKey, out var cached) &&
            cached.ExpiresOn - DateTimeOffset.UtcNow > ExpiryBuffer)
        {
            return cached.Connection;
        }

        var effectiveCredential = credentials is not null
            ? new ClientSecretCredential(credentials.TenantId, credentials.ClientId, credentials.Secret)
            : credential;

        var token = await effectiveCredential.GetTokenAsync(new TokenRequestContext([AdoResourceScope]), ct);
        var conn = new VssConnection(
            new Uri(organizationUrl),
            new VssOAuthAccessTokenCredential(token.Token));

        _cache[cacheKey] = (conn, token.ExpiresOn);
        return conn;
    }
}