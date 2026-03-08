using System.Collections.Concurrent;
using Azure.Core;
using Microsoft.VisualStudio.Services.OAuth;
using Microsoft.VisualStudio.Services.WebApi;

namespace MeisterProPR.Infrastructure.AzureDevOps;

public sealed class VssConnectionFactory(TokenCredential credential)
{
    private const string AdoResourceScope = "499b84ac-1321-427f-aa17-267ca6975798/.default";
    private readonly ConcurrentDictionary<string, VssConnection> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<VssConnection> GetConnectionAsync(string organizationUrl, CancellationToken ct = default)
    {
        if (this._cache.TryGetValue(organizationUrl, out var cached))
        {
            return cached;
        }

        var token = await credential.GetTokenAsync(new TokenRequestContext([AdoResourceScope]), ct);
        var conn = new VssConnection(
            new Uri(organizationUrl),
            new VssOAuthAccessTokenCredential(token.Token));
        this._cache[organizationUrl] = conn;
        return conn;
    }
}