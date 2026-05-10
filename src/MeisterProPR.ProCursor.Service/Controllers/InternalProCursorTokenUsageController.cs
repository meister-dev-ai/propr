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
    /// <summary>
    ///     Returns token-usage totals and series for a client.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="from">Inclusive range start.</param>
    /// <param name="to">Inclusive range end.</param>
    /// <param name="granularity">Aggregation granularity.</param>
    /// <param name="groupBy">Optional breakdown grouping.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <returns>The token-usage response for the client.</returns>
    /// <response code="200">The client token-usage report was returned.</response>
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

    /// <summary>
    ///     Returns the highest-usage sources for a client.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="from">Inclusive range start.</param>
    /// <param name="to">Inclusive range end.</param>
    /// <param name="limit">Maximum number of sources to return.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <returns>The top token-usage sources.</returns>
    /// <response code="200">The top sources were returned.</response>
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

    /// <summary>
    ///     Returns token-usage totals and series for a specific source.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="sourceId">Source identifier.</param>
    /// <param name="from">Inclusive range start.</param>
    /// <param name="to">Inclusive range end.</param>
    /// <param name="granularity">Aggregation granularity.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <returns>The token-usage response for the source.</returns>
    /// <response code="200">The source token-usage report was returned.</response>
    /// <response code="404">The source was not found for the client.</response>
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

    /// <summary>
    ///     Returns recent token-usage events for a source.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="sourceId">Source identifier.</param>
    /// <param name="limit">Maximum number of events to return.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <returns>The recent token-usage events.</returns>
    /// <response code="200">The recent events were returned.</response>
    /// <response code="404">The source was not found for the client.</response>
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

    /// <summary>
    ///     Exports raw token-usage rows for a client and optional source.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="from">Inclusive range start.</param>
    /// <param name="to">Inclusive range end.</param>
    /// <param name="sourceId">Optional source identifier filter.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <returns>The exported token-usage rows.</returns>
    /// <response code="200">The export rows were returned.</response>
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

    /// <summary>
    ///     Returns token-usage freshness metadata for a client.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <returns>The token-usage freshness response.</returns>
    /// <response code="200">The freshness response was returned.</response>
    [HttpGet("/internal/procursor/clients/{clientId:guid}/token-usage/freshness")]
    public async Task<ActionResult<ProCursorTokenUsageFreshnessResponse>> GetFreshness(
        Guid clientId,
        CancellationToken ct = default)
    {
        return this.Ok(await readRepository.GetFreshnessAsync(clientId, ct));
    }

    /// <summary>
    ///     Rebuilds token-usage rollups for a client and time range.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="request">Rebuild request payload.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <returns>The rebuild result.</returns>
    /// <response code="200">The token-usage rollups were rebuilt.</response>
    [HttpPost("/internal/procursor/clients/{clientId:guid}/token-usage/rebuild")]
    public async Task<ActionResult<ProCursorTokenUsageRebuildResponse>> Rebuild(
        Guid clientId,
        [FromBody] ProCursorTokenUsageRebuildRequest request,
        CancellationToken ct = default)
    {
        return this.Ok(await rebuildService.RebuildAsync(clientId, request, ct));
    }
}
