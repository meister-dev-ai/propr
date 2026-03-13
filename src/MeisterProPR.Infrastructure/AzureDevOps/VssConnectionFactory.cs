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
    private readonly ConcurrentDictionary<string, VssConnection> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<VssConnection> GetConnectionAsync(
        string organizationUrl,
        ClientAdoCredentials? credentials = null,
        CancellationToken ct = default)
    {
        var cacheKey = $"{organizationUrl}::{credentials?.ClientId ?? "global"}";
        if (this._cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var effectiveCredential = credentials is not null
            ? new ClientSecretCredential(credentials.TenantId, credentials.ClientId, credentials.Secret)
            : credential;

        var token = await effectiveCredential.GetTokenAsync(new TokenRequestContext([AdoResourceScope]), ct);
        var conn = new VssConnection(
            new Uri(organizationUrl),
            new VssOAuthAccessTokenCredential(token.Token));
        this._cache[cacheKey] = conn;
        return conn;
    }
}