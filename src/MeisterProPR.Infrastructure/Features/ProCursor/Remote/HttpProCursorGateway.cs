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
///     HTTP-backed <see cref="IProCursorGateway" /> implementation used when ProCursor runs out of process.
/// </summary>
public sealed class HttpProCursorGateway(HttpClient httpClient, ILogger<HttpProCursorGateway> logger) : IProCursorGateway
{
    public Task<IReadOnlyList<ProCursorKnowledgeSourceDto>> ListSourcesAsync(Guid clientId, CancellationToken ct = default)
        => this.SendAsync<IReadOnlyList<ProCursorKnowledgeSourceDto>>(HttpMethod.Get, $"internal/procursor/clients/{clientId:D}/sources", null, ct);

    public Task<ProCursorKnowledgeSourceDto> CreateSourceAsync(
        Guid clientId,
        ProCursorKnowledgeSourceRegistrationRequest request,
        CancellationToken ct = default)
        => this.SendAsync<ProCursorKnowledgeSourceDto>(HttpMethod.Post, $"internal/procursor/clients/{clientId:D}/sources", request, ct);

    public Task<ProCursorIndexJobDto> QueueRefreshAsync(
        Guid clientId,
        Guid sourceId,
        ProCursorRefreshRequest request,
        CancellationToken ct = default)
        => this.SendAsync<ProCursorIndexJobDto>(
            HttpMethod.Post,
            $"internal/procursor/clients/{clientId:D}/sources/{sourceId:D}/refresh",
            request,
            ct);

    public Task<IReadOnlyList<ProCursorTrackedBranchDto>> ListTrackedBranchesAsync(
        Guid clientId,
        Guid sourceId,
        CancellationToken ct = default)
        => this.SendAsync<IReadOnlyList<ProCursorTrackedBranchDto>>(
            HttpMethod.Get,
            $"internal/procursor/clients/{clientId:D}/sources/{sourceId:D}/branches",
            null,
            ct);

    public Task<ProCursorTrackedBranchDto> AddTrackedBranchAsync(
        Guid clientId,
        Guid sourceId,
        ProCursorTrackedBranchCreateRequest request,
        CancellationToken ct = default)
        => this.SendAsync<ProCursorTrackedBranchDto>(
            HttpMethod.Post,
            $"internal/procursor/clients/{clientId:D}/sources/{sourceId:D}/branches",
            request,
            ct);

    public Task<ProCursorTrackedBranchDto?> UpdateTrackedBranchAsync(
        Guid clientId,
        Guid sourceId,
        Guid trackedBranchId,
        ProCursorTrackedBranchUpdateRequest request,
        CancellationToken ct = default)
        => this.SendOptionalAsync<ProCursorTrackedBranchDto>(
            HttpMethod.Put,
            $"internal/procursor/clients/{clientId:D}/sources/{sourceId:D}/branches/{trackedBranchId:D}",
            request,
            ct);

    public Task<bool> RemoveTrackedBranchAsync(
        Guid clientId,
        Guid sourceId,
        Guid trackedBranchId,
        CancellationToken ct = default)
        => this.SendDeleteAsync(
            $"internal/procursor/clients/{clientId:D}/sources/{sourceId:D}/branches/{trackedBranchId:D}",
            ct);

    public Task InvalidateRuntimeConfigurationAsync(Guid sourceId, CancellationToken ct = default)
        => this.SendAsync<object>(
            HttpMethod.Post,
            $"internal/procursor/runtime-config/sources/{sourceId:D}/invalidate",
            new { },
            ct);

    public Task<ProCursorKnowledgeAnswerDto> AskKnowledgeAsync(
        ProCursorKnowledgeQueryRequest request,
        CancellationToken ct = default)
        => this.SendAsync<ProCursorKnowledgeAnswerDto>(HttpMethod.Post, "internal/procursor/queries/knowledge", request, ct);

    public Task<ProCursorSymbolInsightDto> GetSymbolInsightAsync(
        ProCursorSymbolQueryRequest request,
        CancellationToken ct = default)
        => this.SendAsync<ProCursorSymbolInsightDto>(HttpMethod.Post, "internal/procursor/queries/symbols", request, ct);

    private async Task<TResponse> SendAsync<TResponse>(
        HttpMethod method,
        string path,
        object? payload,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

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
        return (await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: ct))
               ?? throw new InvalidOperationException($"Remote ProCursor endpoint '{path}' returned an empty payload.");
    }

    private async Task<TResponse?> SendOptionalAsync<TResponse>(
        HttpMethod method,
        string path,
        object? payload,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        using var response = await this.SendCoreAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return default;
        }

        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: ct);
    }

    private async Task<bool> SendDeleteAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        using var response = await this.SendCoreAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new InvalidOperationException(await response.Content.ReadAsStringAsync(ct));
        }

        await EnsureSuccessAsync(response, ct);
        return response.StatusCode == HttpStatusCode.NoContent;
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await httpClient.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Remote ProCursor request failed for {Method} {Uri}", request.Method, request.RequestUri);
            throw new ProCursorDependencyUnavailableException("The configured ProCursor service is unavailable.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Remote ProCursor request timed out for {Method} {Uri}", request.Method, request.RequestUri);
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
            throw new ProCursorDependencyUnavailableException(
                $"The configured ProCursor service returned upstream status {(int)response.StatusCode}.");
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(body)
            ? $"Remote ProCursor request failed with status {(int)response.StatusCode}."
            : body);
    }
}
