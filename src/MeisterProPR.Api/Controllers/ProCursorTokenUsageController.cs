// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using System.Text;
using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>
///     Read-only admin endpoints for ProCursor token usage reporting.
/// </summary>
[ApiController]
public sealed class ProCursorTokenUsageController(
    IProCursorTokenUsageReadRepository readRepository,
    IProCursorTokenUsageRebuildService rebuildService) : ControllerBase
{
    private const int MaxTopSourcesLimit = 1000;

    /// <summary>
    ///     Returns aggregated ProCursor token usage for a client-wide reporting interval.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="from">Inclusive start date in <c>yyyy-MM-dd</c> format.</param>
    /// <param name="to">Inclusive end date in <c>yyyy-MM-dd</c> format.</param>
    /// <param name="granularity">Bucket granularity: <c>daily</c> or <c>monthly</c>.</param>
    /// <param name="groupBy">Optional breakdown mode: <c>source</c> or <c>model</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Aggregated ProCursor client usage returned.</response>
    /// <response code="400">The query parameters were invalid.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not have administrator access to the client.</response>
    [HttpGet("/admin/clients/{clientId:guid}/procursor/token-usage")]
    [ProducesResponseType(typeof(ProCursorTokenUsageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetClientUsage(
        Guid clientId,
        [FromQuery] string from,
        [FromQuery] string to,
        [FromQuery] string granularity = "daily",
        [FromQuery] string? groupBy = null,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        if (!TryParseRange(from, to, out var startDate, out var endDate, out var rangeError))
        {
            this.ModelState.AddModelError("from", rangeError);
            return this.ValidationProblem();
        }

        if (!TryParseGranularity(granularity, out var parsedGranularity))
        {
            this.ModelState.AddModelError(nameof(granularity), "granularity must be 'daily' or 'monthly'.");
            return this.ValidationProblem();
        }

        if (!string.IsNullOrWhiteSpace(groupBy) &&
            !string.Equals(groupBy, "source", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(groupBy, "model", StringComparison.OrdinalIgnoreCase))
        {
            this.ModelState.AddModelError(nameof(groupBy), "groupBy must be 'source' or 'model' when provided.");
            return this.ValidationProblem();
        }

        var response = await readRepository.GetClientUsageAsync(clientId, startDate, endDate, parsedGranularity, groupBy, ct);
        return this.Ok(response);
    }

    /// <summary>
    ///     Returns the highest-consuming ProCursor knowledge sources for a selected client period.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="period">Relative period such as <c>30d</c>, <c>90d</c>, or <c>365d</c>.</param>
    /// <param name="limit">Maximum number of ranked items to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Ranked top sources returned.</response>
    /// <response code="400">The period or limit was invalid.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not have administrator access to the client.</response>
    [HttpGet("/admin/clients/{clientId:guid}/procursor/token-usage/top-sources")]
    [ProducesResponseType(typeof(ProCursorTopSourcesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetTopSources(
        Guid clientId,
        [FromQuery] string period,
        [FromQuery] int limit = 10,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        if (!TryParsePeriod(period, out var from, out var to))
        {
            this.ModelState.AddModelError(nameof(period), "period must use the format '<days>d', for example '30d'.");
            return this.ValidationProblem();
        }

        if (limit <= 0)
        {
            this.ModelState.AddModelError(nameof(limit), "limit must be greater than zero.");
            return this.ValidationProblem();
        }

        if (limit > MaxTopSourcesLimit)
        {
            this.ModelState.AddModelError(nameof(limit), $"limit must be less than or equal to {MaxTopSourcesLimit}.");
            return this.ValidationProblem();
        }

        var items = await readRepository.GetTopSourcesAsync(clientId, from, to, limit, ct);
        return this.Ok(new ProCursorTopSourcesResponse(clientId, period, items));
    }

    /// <summary>
    ///     Returns aggregated ProCursor token usage for one knowledge source.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="sourceId">Knowledge source identifier.</param>
    /// <param name="period">Optional relative period such as <c>30d</c>.</param>
    /// <param name="from">Optional inclusive start date in <c>yyyy-MM-dd</c> format.</param>
    /// <param name="to">Optional inclusive end date in <c>yyyy-MM-dd</c> format.</param>
    /// <param name="granularity">Bucket granularity: <c>daily</c> or <c>monthly</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Source-level ProCursor usage returned.</response>
    /// <response code="400">The query parameters were invalid.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not have administrator access to the client.</response>
    /// <response code="404">The requested source was not found for the client.</response>
    [HttpGet("/admin/clients/{clientId:guid}/procursor/sources/{sourceId:guid}/token-usage")]
    [ProducesResponseType(typeof(ProCursorSourceTokenUsageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSourceUsage(
        Guid clientId,
        Guid sourceId,
        [FromQuery] string? period = null,
        [FromQuery] string? from = null,
        [FromQuery] string? to = null,
        [FromQuery] string granularity = "daily",
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        if (!TryResolveRange(period, from, to, out var startDate, out var endDate, out var rangeError))
        {
            this.ModelState.AddModelError(nameof(period), rangeError);
            return this.ValidationProblem();
        }

        if (!TryParseGranularity(granularity, out var parsedGranularity))
        {
            this.ModelState.AddModelError(nameof(granularity), "granularity must be 'daily' or 'monthly'.");
            return this.ValidationProblem();
        }

        var response = await readRepository.GetSourceUsageAsync(clientId, sourceId, startDate, endDate, parsedGranularity, ct);
        return response is null ? this.NotFound() : this.Ok(response);
    }

    /// <summary>
    ///     Returns recent safe ProCursor usage events for one knowledge source.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="sourceId">Knowledge source identifier.</param>
    /// <param name="limit">Maximum number of recent events to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Recent usage events returned.</response>
    /// <response code="400">The limit was invalid.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not have administrator access to the client.</response>
    /// <response code="404">The requested source was not found for the client.</response>
    [HttpGet("/admin/clients/{clientId:guid}/procursor/sources/{sourceId:guid}/token-usage/events")]
    [ProducesResponseType(typeof(ProCursorTokenUsageEventsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRecentEvents(
        Guid clientId,
        Guid sourceId,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        if (limit <= 0)
        {
            this.ModelState.AddModelError(nameof(limit), "limit must be greater than zero.");
            return this.ValidationProblem();
        }

        var response = await readRepository.GetRecentEventsAsync(clientId, sourceId, limit, ct);
        return response is null ? this.NotFound() : this.Ok(response);
    }

    /// <summary>
    ///     Exports ProCursor token usage rows as CSV for the selected interval.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="from">Inclusive start date in <c>yyyy-MM-dd</c> format.</param>
    /// <param name="to">Inclusive end date in <c>yyyy-MM-dd</c> format.</param>
    /// <param name="sourceId">Optional knowledge source filter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">CSV export returned.</response>
    /// <response code="400">The query parameters were invalid.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not have administrator access to the client.</response>
    [HttpGet("/admin/clients/{clientId:guid}/procursor/token-usage/export")]
    [Produces("text/csv")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Export(
        Guid clientId,
        [FromQuery] string from,
        [FromQuery] string to,
        [FromQuery] Guid? sourceId = null,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        if (!TryParseRange(from, to, out var startDate, out var endDate, out var rangeError))
        {
            this.ModelState.AddModelError("from", rangeError);
            return this.ValidationProblem();
        }

        var rows = await readRepository.ExportAsync(clientId, startDate, endDate, sourceId, ct);
        var csv = BuildCsv(rows);
        return this.File(
            Encoding.UTF8.GetBytes(csv),
            "text/csv",
            $"procursor-token-usage-{clientId:N}-{startDate:yyyyMMdd}-{endDate:yyyyMMdd}.csv");
    }

    /// <summary>
    ///     Returns freshness metadata for ProCursor token usage rollups.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Freshness metadata returned.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not have administrator access to the client.</response>
    [HttpGet("/admin/clients/{clientId:guid}/procursor/token-usage/freshness")]
    [ProducesResponseType(typeof(ProCursorTokenUsageFreshnessResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetFreshness(Guid clientId, CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        return this.Ok(await readRepository.GetFreshnessAsync(clientId, ct));
    }

    /// <summary>
    ///     Rebuilds ProCursor token usage rollups for the selected captured interval.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="request">Rebuild request payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Rollups rebuilt successfully.</response>
    /// <response code="400">The request payload was invalid.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not have administrator access to the client.</response>
    [HttpPost("/admin/clients/{clientId:guid}/procursor/token-usage/rebuild")]
    [ProducesResponseType(typeof(ProCursorTokenUsageRebuildResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Rebuild(
        Guid clientId,
        [FromBody] ProCursorTokenUsageRebuildRequest request,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        if (request.To < request.From)
        {
            this.ModelState.AddModelError(nameof(request.To), "to must be greater than or equal to from.");
            return this.ValidationProblem();
        }

        return this.Ok(await rebuildService.RebuildAsync(clientId, request, ct));
    }

    private static bool TryResolveRange(
        string? period,
        string? from,
        string? to,
        out DateOnly startDate,
        out DateOnly endDate,
        out string error)
    {
        if (!string.IsNullOrWhiteSpace(period))
        {
            if (TryParsePeriod(period, out startDate, out endDate))
            {
                error = string.Empty;
                return true;
            }

            startDate = default;
            endDate = default;
            error = "period must use the format '<days>d', for example '30d'.";
            return false;
        }

        return TryParseRange(from, to, out startDate, out endDate, out error);
    }

    private static bool TryParseRange(
        string? from,
        string? to,
        out DateOnly startDate,
        out DateOnly endDate,
        out string error)
    {
        startDate = default;
        endDate = default;

        if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out startDate) ||
            !DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out endDate))
        {
            error = "from and to must use yyyy-MM-dd format.";
            return false;
        }

        if (endDate < startDate)
        {
            error = "to must be greater than or equal to from.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool TryParsePeriod(string? period, out DateOnly from, out DateOnly to)
    {
        from = default;
        to = default;

        if (string.IsNullOrWhiteSpace(period) || period.Length < 2 || !period.EndsWith("d", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!int.TryParse(period[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) || days <= 0)
        {
            return false;
        }

        to = DateOnly.FromDateTime(DateTime.UtcNow);
        from = to.AddDays(-(days - 1));
        return true;
    }

    private static bool TryParseGranularity(string value, out ProCursorTokenUsageGranularity granularity)
    {
        if (string.Equals(value, "monthly", StringComparison.OrdinalIgnoreCase))
        {
            granularity = ProCursorTokenUsageGranularity.Monthly;
            return true;
        }

        if (string.Equals(value, "daily", StringComparison.OrdinalIgnoreCase))
        {
            granularity = ProCursorTokenUsageGranularity.Daily;
            return true;
        }

        granularity = default;
        return false;
    }

    private static string BuildCsv(IReadOnlyList<ProCursorTokenUsageExportRowDto> rows)
    {
        var builder = new StringBuilder();
        builder.AppendLine("date,sourceId,sourceDisplayName,modelName,callType,prompt_tokens,completion_tokens,total_tokens,estimated_cost_usd,tokens_estimated,index_job_id,source_path,resource_id,knowledge_chunk_id");

        foreach (var row in rows)
        {
            builder.Append(Escape(row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))).Append(',')
                .Append(Escape(row.SourceId?.ToString())).Append(',')
                .Append(Escape(row.SourceDisplayName)).Append(',')
                .Append(Escape(row.ModelName)).Append(',')
                .Append(Escape(row.CallType.ToString().ToLowerInvariant())).Append(',')
                .Append(row.PromptTokens.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.CompletionTokens.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.TotalTokens.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(row.EstimatedCostUsd?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append(',')
                .Append(row.TokensEstimated ? "true" : "false").Append(',')
                .Append(Escape(row.IndexJobId?.ToString())).Append(',')
                .Append(Escape(row.SourcePath)).Append(',')
                .Append(Escape(row.ResourceId)).Append(',')
                .Append(Escape(row.KnowledgeChunkId?.ToString()))
                .AppendLine();
        }

        return builder.ToString();
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (!value.Contains(",", StringComparison.Ordinal) &&
            !value.Contains("\"", StringComparison.Ordinal) &&
            !value.Contains("\n", StringComparison.Ordinal) &&
            !value.Contains("\r", StringComparison.Ordinal))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
