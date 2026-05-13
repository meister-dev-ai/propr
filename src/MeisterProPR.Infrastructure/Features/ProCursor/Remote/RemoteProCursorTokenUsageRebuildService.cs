// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.ProCursor.Remote;

/// <summary>
///     Remote ProCursor rollup rebuild client used when ProPR runs against an extracted ProCursor host.
/// </summary>
public sealed class RemoteProCursorTokenUsageRebuildService(
    HttpClient httpClient,
    ILogger<RemoteProCursorTokenUsageRebuildService> logger) : IProCursorTokenUsageRebuildService
{
    public async Task<ProCursorTokenUsageRebuildResponse> RebuildAsync(
        Guid clientId,
        ProCursorTokenUsageRebuildRequest request,
        CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"internal/procursor/clients/{clientId:D}/token-usage/rebuild")
        {
            Content = JsonContent.Create(request),
        };

        using var response = await this.SendCoreAsync(message, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<ProCursorTokenUsageRebuildResponse>(ct)
               ?? throw new InvalidOperationException("Remote ProCursor rebuild endpoint returned an empty payload.");
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await httpClient.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Remote ProCursor rebuild request failed for {Method} {Uri}", request.Method, request.RequestUri);
            throw new ProCursorDependencyUnavailableException("The configured ProCursor service is unavailable.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Remote ProCursor rebuild request timed out for {Method} {Uri}", request.Method, request.RequestUri);
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
                ? $"Remote ProCursor rebuild request failed with status {(int)response.StatusCode}."
                : body);
    }
}
