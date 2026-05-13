// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.ProCursor.Remote;

/// <summary>
///     Remote ProCursor token-usage read client used when ProPR runs against an extracted ProCursor host.
/// </summary>
public sealed class RemoteProCursorTokenUsageReadRepository(
    HttpClient httpClient,
    ILogger<RemoteProCursorTokenUsageReadRepository> logger) : IProCursorTokenUsageReadRepository
{
    public Task<ProCursorTokenUsageResponse> GetClientUsageAsync(
        Guid clientId,
        DateOnly from,
        DateOnly to,
        ProCursorTokenUsageGranularity granularity,
        string? groupBy,
        CancellationToken ct = default)
    {
        return this.SendAsync<ProCursorTokenUsageResponse>(
            BuildPath(
                $"internal/procursor/clients/{clientId:D}/token-usage",
                ("from", FormatDate(from)),
                ("to", FormatDate(to)),
                ("granularity", FormatGranularity(granularity)),
                ("groupBy", groupBy)),
            ct);
    }

    public Task<ProCursorSourceTokenUsageResponse?> GetSourceUsageAsync(
        Guid clientId,
        Guid sourceId,
        DateOnly from,
        DateOnly to,
        ProCursorTokenUsageGranularity granularity,
        CancellationToken ct = default)
    {
        return this.SendOptionalAsync<ProCursorSourceTokenUsageResponse>(
            BuildPath(
                $"internal/procursor/clients/{clientId:D}/sources/{sourceId:D}/token-usage",
                ("from", FormatDate(from)),
                ("to", FormatDate(to)),
                ("granularity", FormatGranularity(granularity))),
            ct);
    }

    public Task<IReadOnlyList<ProCursorTopSourceUsageDto>> GetTopSourcesAsync(
        Guid clientId,
        DateOnly from,
        DateOnly to,
        int limit,
        CancellationToken ct = default)
    {
        return this.SendAsync<IReadOnlyList<ProCursorTopSourceUsageDto>>(
            BuildPath(
                $"internal/procursor/clients/{clientId:D}/token-usage/top-sources",
                ("from", FormatDate(from)),
                ("to", FormatDate(to)),
                ("limit", limit.ToString(CultureInfo.InvariantCulture))),
            ct);
    }

    public Task<ProCursorTokenUsageEventsResponse?> GetRecentEventsAsync(
        Guid clientId,
        Guid sourceId,
        int limit,
        CancellationToken ct = default)
    {
        return this.SendOptionalAsync<ProCursorTokenUsageEventsResponse>(
            BuildPath(
                $"internal/procursor/clients/{clientId:D}/sources/{sourceId:D}/token-usage/events",
                ("limit", limit.ToString(CultureInfo.InvariantCulture))),
            ct);
    }

    public Task<IReadOnlyList<ProCursorTokenUsageExportRowDto>> ExportAsync(
        Guid clientId,
        DateOnly from,
        DateOnly to,
        Guid? sourceId,
        CancellationToken ct = default)
    {
        return this.SendAsync<IReadOnlyList<ProCursorTokenUsageExportRowDto>>(
            BuildPath(
                $"internal/procursor/clients/{clientId:D}/token-usage/export",
                ("from", FormatDate(from)),
                ("to", FormatDate(to)),
                ("sourceId", sourceId?.ToString("D"))),
            ct);
    }

    public Task<ProCursorTokenUsageFreshnessResponse> GetFreshnessAsync(Guid clientId, CancellationToken ct = default)
    {
        return this.SendAsync<ProCursorTokenUsageFreshnessResponse>(
            $"internal/procursor/clients/{clientId:D}/token-usage/freshness",
            ct);
    }

    private async Task<TResponse> SendAsync<TResponse>(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await this.SendCoreAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new KeyNotFoundException();
        }

        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<TResponse>(ct)
               ?? throw new InvalidOperationException($"Remote ProCursor endpoint '{path}' returned an empty payload.");
    }

    private async Task<TResponse?> SendOptionalAsync<TResponse>(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await this.SendCoreAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return default;
        }

        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<TResponse>(ct);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await httpClient.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Remote ProCursor reporting request failed for {Method} {Uri}", request.Method, request.RequestUri);
            throw new ProCursorDependencyUnavailableException("The configured ProCursor service is unavailable.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Remote ProCursor reporting request timed out for {Method} {Uri}", request.Method, request.RequestUri);
            throw new ProCursorDependencyUnavailableException("The configured ProCursor service did not respond in time.", ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new ProCursorDependencyUnavailableException("The configured ProCursor service rejected the shared access credential.");
        }

        if ((int)response.StatusCode >= 500)
        {
            throw new ProCursorDependencyUnavailableException($"The configured ProCursor service returned upstream status {(int)response.StatusCode}.");
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(body)
                ? $"Remote ProCursor request failed with status {(int)response.StatusCode}."
                : body);
    }

    private static string BuildPath(string basePath, params (string Key, string? Value)[] query)
    {
        var parts = query
            .Where(item => !string.IsNullOrWhiteSpace(item.Value))
            .Select(item => $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value!)}")
            .ToArray();

        return parts.Length == 0 ? basePath : $"{basePath}?{string.Join("&", parts)}";
    }

    private static string FormatDate(DateOnly value)
    {
        return value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string FormatGranularity(ProCursorTokenUsageGranularity granularity)
    {
        return granularity == ProCursorTokenUsageGranularity.Monthly ? "monthly" : "daily";
    }
}
