// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Json;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.ProCursor.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeisterProPR.ProCursor.Infrastructure.Remote;

/// <summary>
///     HTTP-backed runtime configuration broker used by the extracted ProCursor runtime.
/// </summary>
public sealed class ProPrRuntimeConfigurationBroker(
    HttpClient httpClient,
    ILogger<ProPrRuntimeConfigurationBroker> logger,
    IOptions<ProCursorHostOptions> hostOptions)
    : IProCursorRuntimeConfigurationBroker
{
    public async Task<IReadOnlyList<ProCursorRuntimeConfigurationProjectionDto>> ListEnabledAsync(CancellationToken ct = default)
    {
        using var response = await this.SendCoreAsync(new HttpRequestMessage(
            HttpMethod.Get,
            ProPrBrokerRequestUri.Create(hostOptions, "internal/propr/procursor/runtime-config/enabled")), ct);

        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<IReadOnlyList<ProCursorRuntimeConfigurationProjectionDto>>(
                   ProCursorRemoteJson.SerializerOptions,
                   ct))
               ?? [];
    }

    public async Task<ProCursorRuntimeConfigurationProjectionDto> RefreshAsync(
        Guid sourceId,
        ProCursorRuntimeConfigurationRefreshRequest request,
        CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            ProPrBrokerRequestUri.Create(hostOptions, $"internal/propr/procursor/runtime-config/sources/{sourceId:D}/refresh"))
        {
            Content = JsonContent.Create(request),
        };

        using var response = await this.SendCoreAsync(message, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new KeyNotFoundException();
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new InvalidOperationException(await response.Content.ReadAsStringAsync(ct));
        }

        await EnsureSuccessAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<ProCursorRuntimeConfigurationProjectionDto>(
                   ProCursorRemoteJson.SerializerOptions,
                   ct))
               ?? throw new InvalidOperationException("ProPR runtime configuration broker returned an empty payload.");
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await httpClient.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "ProPR runtime configuration broker request failed for {Method} {Uri}", request.Method, request.RequestUri);
            throw new ProCursorDependencyUnavailableException("The configured ProPR broker is unavailable.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "ProPR runtime configuration broker request timed out for {Method} {Uri}", request.Method, request.RequestUri);
            throw new ProCursorDependencyUnavailableException("The configured ProPR broker did not respond in time.", ex);
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
            throw new ProCursorDependencyUnavailableException("The configured ProPR broker rejected the shared access credential.");
        }

        if ((int)response.StatusCode >= 500)
        {
            throw new ProCursorDependencyUnavailableException(
                $"The configured ProPR broker returned upstream status {(int)response.StatusCode}.");
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(body)
            ? $"ProPR broker request failed with status {(int)response.StatusCode}."
            : body);
    }
}
