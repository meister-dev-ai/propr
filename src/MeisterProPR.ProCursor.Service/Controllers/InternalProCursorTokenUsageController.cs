// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.ProCursor.Service.Controllers;

/// <summary>
///     Internal token-usage reporting and rebuild endpoints for the extracted ProCursor host.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = MeisterProPR.Infrastructure.Features.ProCursor.Remote.ProCursorSharedKeyAuthenticationDefaults.Scheme)]
public sealed class InternalProCursorTokenUsageController(
    IProCursorTokenUsageReadRepository readRepository,
    IProCursorTokenUsageRebuildService rebuildService) : ControllerBase
{
    [HttpGet("/internal/procursor/clients/{clientId:guid}/token-usage")]
    public async Task<ActionResult<ProCursorTokenUsageResponse>> GetClientUsage(
        Guid clientId,
        [FromQuery] DateOnly from,
        [FromQuery] DateOnly to,
        [FromQuery] ProCursorTokenUsageGranularity granularity = ProCursorTokenUsageGranularity.Daily,
        [FromQuery] string? groupBy = null,
        CancellationToken ct = default)
    {
        return this.Ok(await readRepository.GetClientUsageAsync(clientId, from, to, granularity, groupBy, ct));
    }

    [HttpGet("/internal/procursor/clients/{clientId:guid}/token-usage/top-sources")]
    public async Task<ActionResult<IReadOnlyList<ProCursorTopSourceUsageDto>>> GetTopSources(
        Guid clientId,
        [FromQuery] DateOnly from,
        [FromQuery] DateOnly to,
        [FromQuery] int limit = 10,
        CancellationToken ct = default)
    {
        return this.Ok(await readRepository.GetTopSourcesAsync(clientId, from, to, limit, ct));
    }

    [HttpGet("/internal/procursor/clients/{clientId:guid}/sources/{sourceId:guid}/token-usage")]
    public async Task<ActionResult<ProCursorSourceTokenUsageResponse>> GetSourceUsage(
        Guid clientId,
        Guid sourceId,
        [FromQuery] DateOnly from,
        [FromQuery] DateOnly to,
        [FromQuery] ProCursorTokenUsageGranularity granularity = ProCursorTokenUsageGranularity.Daily,
        CancellationToken ct = default)
    {
        var response = await readRepository.GetSourceUsageAsync(clientId, sourceId, from, to, granularity, ct);
        return response is null ? this.NotFound() : this.Ok(response);
    }

    [HttpGet("/internal/procursor/clients/{clientId:guid}/sources/{sourceId:guid}/token-usage/events")]
    public async Task<ActionResult<ProCursorTokenUsageEventsResponse>> GetRecentEvents(
        Guid clientId,
        Guid sourceId,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var response = await readRepository.GetRecentEventsAsync(clientId, sourceId, limit, ct);
        return response is null ? this.NotFound() : this.Ok(response);
    }

    [HttpGet("/internal/procursor/clients/{clientId:guid}/token-usage/export")]
    public async Task<ActionResult<IReadOnlyList<ProCursorTokenUsageExportRowDto>>> Export(
        Guid clientId,
        [FromQuery] DateOnly from,
        [FromQuery] DateOnly to,
        [FromQuery] Guid? sourceId = null,
        CancellationToken ct = default)
    {
        return this.Ok(await readRepository.ExportAsync(clientId, from, to, sourceId, ct));
    }

    [HttpGet("/internal/procursor/clients/{clientId:guid}/token-usage/freshness")]
    public async Task<ActionResult<ProCursorTokenUsageFreshnessResponse>> GetFreshness(
        Guid clientId,
        CancellationToken ct = default)
    {
        return this.Ok(await readRepository.GetFreshnessAsync(clientId, ct));
    }

    [HttpPost("/internal/procursor/clients/{clientId:guid}/token-usage/rebuild")]
    public async Task<ActionResult<ProCursorTokenUsageRebuildResponse>> Rebuild(
        Guid clientId,
        [FromBody] ProCursorTokenUsageRebuildRequest request,
        CancellationToken ct = default)
    {
        return this.Ok(await rebuildService.RebuildAsync(clientId, request, ct));
    }
}
