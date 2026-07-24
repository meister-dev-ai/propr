// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Exposes daily token consumption aggregates for a client.</summary>
[ApiController]
[Route("admin/clients/{clientId:guid}/token-usage")]
public sealed partial class ClientTokenUsageController(
    IClientTokenUsageRepository tokenUsageRepository,
    ILogger<ClientTokenUsageController> logger) : ControllerBase
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Token usage queried for client {ClientId} from {From} to {To}")]
    private static partial void LogTokenUsageQueried(ILogger logger, Guid clientId, DateOnly from, DateOnly to);

    /// <summary>Returns daily token consumption samples for the specified client within the given date range.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="from">Start date (inclusive, YYYY-MM-DD).</param>
    /// <param name="to">End date (inclusive, YYYY-MM-DD).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Token usage data grouped by (model, day).</response>
    /// <response code="400">Date parameters are missing or invalid.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not have access to this client.</response>
    [HttpGet]
    [ProducesResponseType(typeof(ClientTokenUsageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetTokenUsage(
        Guid clientId,
        [FromQuery] string? from,
        [FromQuery] string? to,
        CancellationToken ct = default)
    {
        var roleCheck = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser);
        if (roleCheck is not null)
        {
            return roleCheck;
        }

        // Exact-format parse so the accepted input matches the advertised YYYY-MM-DD contract (TryParse with a culture
        // still accepts other invariant date spellings, which is more permissive than the documented format).
        if (!DateOnly.TryParseExact(from, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fromDate))
        {
            return this.BadRequest(new { error = "Query parameter 'from' is required and must be a valid date (YYYY-MM-DD)." });
        }

        if (!DateOnly.TryParseExact(to, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var toDate))
        {
            return this.BadRequest(new { error = "Query parameter 'to' is required and must be a valid date (YYYY-MM-DD)." });
        }

        if (fromDate > toDate)
        {
            return this.BadRequest(new { error = "'from' date must not be after 'to' date." });
        }

        var samples = await tokenUsageRepository.GetByClientAndDateRangeAsync(clientId, fromDate, toDate, ct);

        var sampleDtos = samples
            .Select(s => new ClientTokenUsageSampleDto(
                s.ModelId,
                s.Date,
                s.InputTokens,
                s.OutputTokens,
                s.CachedInputTokens,
                s.CacheWriteTokens,
                s.ReasoningTokens,
                s.EstimatedCostUsd,
                s.LogicalModelName))
            .ToList();

        var totalEstimatedCostUsd = sampleDtos.Any(s => s.EstimatedCostUsd.HasValue)
            ? sampleDtos.Sum(s => s.EstimatedCostUsd ?? 0m)
            : (decimal?)null;

        var dto = new ClientTokenUsageDto(
            clientId,
            fromDate,
            toDate,
            sampleDtos.Sum(s => s.InputTokens),
            sampleDtos.Sum(s => s.OutputTokens),
            sampleDtos,
            sampleDtos.Sum(s => s.CachedInputTokens),
            sampleDtos.Sum(s => s.CacheWriteTokens),
            sampleDtos.Sum(s => s.ReasoningTokens),
            totalEstimatedCostUsd);

        LogTokenUsageQueried(logger, clientId, fromDate, toDate);
        return this.Ok(dto);
    }
}
