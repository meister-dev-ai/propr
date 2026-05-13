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
///     HTTP-backed embedding broker used by the extracted ProCursor runtime.
/// </summary>
public sealed class ProPrEmbeddingBroker(
    HttpClient httpClient,
    ILogger<ProPrEmbeddingBroker> logger,
    IOptions<ProCursorHostOptions> hostOptions)
    : IProCursorEmbeddingBroker
{
    public Task<ProCursorEmbeddingDeploymentDto> GetDeploymentAsync(
        Guid clientId,
        int? expectedDimensions = null,
        CancellationToken ct = default)
    {
        return this.SendAsync<ProCursorEmbeddingDeploymentDto>(
            "internal/propr/procursor/broker/embeddings/deployment",
            new ProCursorEmbeddingDeploymentRequest(clientId, expectedDimensions),
            ct);
    }

    public Task<ProCursorEmbeddingBatchResponse> GenerateEmbeddingsAsync(
        Guid clientId,
        IReadOnlyList<string> inputs,
        int? expectedDimensions = null,
        CancellationToken ct = default)
    {
        return this.SendAsync<ProCursorEmbeddingBatchResponse>(
            "internal/propr/procursor/broker/embeddings/generate",
            new ProCursorEmbeddingBatchRequest(clientId, inputs, expectedDimensions),
            ct);
    }

    private async Task<TResponse> SendAsync<TResponse>(string path, object payload, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ProPrBrokerRequestUri.Create(hostOptions, path))
        {
            Content = JsonContent.Create(payload),
        };

        using var response = await this.SendCoreAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new KeyNotFoundException();
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new InvalidOperationException(await response.Content.ReadAsStringAsync(ct));
        }

        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<TResponse>(ProCursorRemoteJson.SerializerOptions, ct)
               ?? throw new InvalidOperationException($"ProPR broker endpoint '{path}' returned an empty payload.");
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await httpClient.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "ProPR embedding broker request failed for {Method} {Uri}", request.Method, request.RequestUri);
            throw new ProCursorDependencyUnavailableException("The configured ProPR broker is unavailable.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "ProPR embedding broker request timed out for {Method} {Uri}", request.Method, request.RequestUri);
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
            throw new ProCursorDependencyUnavailableException($"The configured ProPR broker returned upstream status {(int)response.StatusCode}.");
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(body)
                ? $"ProPR broker request failed with status {(int)response.StatusCode}."
                : body);
    }
}
