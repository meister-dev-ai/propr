// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using MeisterProPR.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.AzureDevOps;

public sealed partial class AdoTokenValidator(
    IHttpClientFactory httpClientFactory,
    TokenCredential serverCredential,
    ILogger<AdoTokenValidator> logger) : IAdoTokenValidator
{
    private const string AdoScope = "499b84ac-1321-427f-aa17-267ca6975798/.default";

    private const string GlobalConnectionDataUrl =
        "https://app.vssps.visualstudio.com/_apis/connectionData?api-version=7.1";

    public async Task<bool> IsValidAsync(string adoToken, string? orgUrl = null, CancellationToken ct = default)
    {
        if (IsJwt(adoToken) && !string.IsNullOrWhiteSpace(orgUrl))
        {
            return await this.ValidateExtensionTokenAsync(adoToken, orgUrl, ct);
        }

        // PAT path: Basic auth against global connectionData
        var client = httpClientFactory.CreateClient("AdoTokenValidator");
        var encoded = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{adoToken}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, GlobalConnectionDataUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
        using var response = await client.SendAsync(request, ct);

        this.LogValidationResult("Basic", (int)response.StatusCode);
        return response.StatusCode == HttpStatusCode.OK;
    }

    private static string? ExtractNameIdFromJwt(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2)
            {
                return null;
            }

            var payload = parts[1];
            var padded = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var bytes = Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/'));
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(bytes));
            var root = doc.RootElement;

            // nameid is the VSS identity ID; aui is authenticated user identity — both are valid.
            foreach (var claim in new[] { "nameid", "aui", "sub" })
            {
                if (root.TryGetProperty(claim, out var val) && val.ValueKind == JsonValueKind.String)
                {
                    return val.GetString();
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string ExtractOrgName(string orgUrl)
    {
        var uri = new Uri(orgUrl.TrimEnd('/'));
        return uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase)
            ? uri.Segments.Last().TrimEnd('/')
            : uri.Host.Split('.')[0];
    }

    private static bool IsJwt(string token)
    {
        return token.StartsWith("eyJ", StringComparison.Ordinal);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "ADO JWT token missing upn/unique_name/email claim — cannot validate.")]
    private partial void LogJwtClaimMissing();

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "ADO token validation: scheme={Scheme} status={StatusCode}")]
    private partial void LogValidationResult(string scheme, int statusCode);

    /// <summary>
    ///     Validates a browser-extension JWT by decoding its UPN claim and confirming
    ///     the user exists in the org using the server's own service-principal credentials.
    ///     This avoids calling VSSPS with the user token, which fails for session tokens.
    /// </summary>
    private async Task<bool> ValidateExtensionTokenAsync(string token, string orgUrl, CancellationToken ct)
    {
        var nameId = ExtractNameIdFromJwt(token);
        if (nameId is null)
        {
            this.LogJwtClaimMissing();
            return false;
        }

        var orgName = ExtractOrgName(orgUrl);
        var serverToken = await serverCredential.GetTokenAsync(new TokenRequestContext([AdoScope]), ct);

        // If nameid is a GUID, use the exact identityIds lookup; otherwise fall back to General search.
        var url = Guid.TryParse(nameId, out _)
            ? $"https://vssps.dev.azure.com/{orgName}/_apis/identities?identityIds={nameId}&api-version=7.1"
            : $"https://vssps.dev.azure.com/{orgName}/_apis/identities?searchFilter=General&filterValue={Uri.EscapeDataString(nameId)}&api-version=7.1";

        var client = httpClientFactory.CreateClient("AdoTokenValidator");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serverToken.Token);
        using var response = await client.SendAsync(request, ct);

        if (response.StatusCode != HttpStatusCode.OK)
        {
            this.LogValidationResult("ServerBearer/Identity", (int)response.StatusCode);
            return false;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var count = doc.RootElement.TryGetProperty("count", out var countEl) ? countEl.GetInt32() : 0;

        this.LogValidationResult("ServerBearer/Identity", (int)response.StatusCode);
        return count > 0;
    }
}
