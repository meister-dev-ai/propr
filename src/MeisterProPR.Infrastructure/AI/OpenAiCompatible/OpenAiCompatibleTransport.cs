// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterProPR.Application.DTOs;

namespace MeisterProPR.Infrastructure.AI.OpenAiCompatible;

/// <summary>
///     Executes OpenAI-compatible admin operations over HTTP.
/// </summary>
public sealed class OpenAiCompatibleTransport(
    IHttpClientFactory httpClientFactory,
    OpenAiCompatibleRequestFactory requestFactory)
{
    public async Task<(HttpStatusCode StatusCode, IReadOnlyList<string> Models, string? ErrorMessage)> DiscoverModelsAsync(
        AiConnectionProbeOptionsDto options,
        CancellationToken ct = default)
    {
        using var request = requestFactory.CreateModelsRequest(options);
        using var response = await httpClientFactory.CreateClient("AiProviderAdmin").SendAsync(request, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            return (response.StatusCode, [], string.IsNullOrWhiteSpace(payload) ? response.ReasonPhrase : payload);
        }

        return (response.StatusCode, ParseModelIds(payload), null);
    }

    private static IReadOnlyList<string> ParseModelIds(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }

        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return dataElement.EnumerateArray()
            .Select(element => element.TryGetProperty("id", out var idElement) ? idElement.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();
    }
}
