using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Infrastructure.AzureDevOps;

/// <summary>
///     ADO-backed implementation of <see cref="IIdentityResolver" /> that calls the
///     org-scoped VSSPS identity endpoint, which the service principal's token is
///     authorised to access (unlike the global <c>vssps.visualstudio.com</c> endpoint).
/// </summary>
public sealed class AdoIdentityResolver(
    TokenCredential credential,
    IHttpClientFactory httpClientFactory,
    IClientAdoCredentialRepository credentialRepository)
    : IIdentityResolver
{
    private const string AdoScope = "499b84ac-1321-427f-aa17-267ca6975798/.default";

    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    /// <inheritdoc />
    public async Task<IReadOnlyList<ResolvedIdentity>> ResolveAsync(
        string organizationUrl,
        string displayName,
        Guid clientId,
        CancellationToken ct = default)
    {
        var perClientCredentials = await credentialRepository.GetByClientIdAsync(clientId, ct);

        var effectiveCredential = perClientCredentials is not null
            ? new ClientSecretCredential(
                perClientCredentials.TenantId,
                perClientCredentials.ClientId,
                perClientCredentials.Secret)
            : credential;

        var token = await effectiveCredential.GetTokenAsync(new TokenRequestContext([AdoScope]), ct);

        // Extract org name from https://dev.azure.com/{org}
        var orgName = new Uri(organizationUrl).Segments.Last().TrimEnd('/');
        var url = $"https://vssps.dev.azure.com/{orgName}/_apis/identities" +
                  $"?searchFilter=General&filterValue={Uri.EscapeDataString(displayName)}&api-version=7.1";

        var client = httpClientFactory.CreateClient("AdoIdentity");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token.Token);

        var response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<IdentityQueryResult>(body, JsonOptions);

        return result?.Value?
            .Where(i => !string.IsNullOrWhiteSpace(i.ProviderDisplayName))
            .Select(i => new ResolvedIdentity(i.Id, i.ProviderDisplayName!))
            .ToList() ?? [];
    }

    private sealed class IdentityQueryResult
    {
        public List<IdentityEntry>? Value { get; set; }
    }

    private sealed class IdentityEntry
    {
        public Guid Id { get; set; }
        public string? ProviderDisplayName { get; set; }
    }
}
