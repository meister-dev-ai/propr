// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Globalization;
using MeisterProPR.Api.Extensions;
using MeisterProPR.Api.Features.Licensing;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Budgeting;
using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;
using MeisterProPR.Application.Features.Licensing.Support;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Exposes a client's USD spend against its monthly budget, with a trajectory projection and history.</summary>
[ApiController]
[Route("admin/clients/{clientId:guid}/budget")]
public sealed partial class ClientBudgetConsumptionController(
    IClientBudgetConsumptionService consumptionService,
    ILogger<ClientBudgetConsumptionController> logger,
    ILicensingCapabilityService? licensingCapabilityService = null) : ControllerBase
{
    private const int DefaultHistoryMonths = 12;

    [LoggerMessage(Level = LogLevel.Information, Message = "Budget consumption queried for client {ClientId}")]
    private static partial void LogBudgetConsumptionQueried(ILogger logger, Guid clientId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Budget history queried for client {ClientId}")]
    private static partial void LogBudgetHistoryQueried(ILogger logger, Guid clientId);

    /// <summary>Returns the client's monthly budget consumption and forecast for a period (the current month by default).</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="period">Optional target month as <c>YYYY-MM</c>; omit for the current month. A past month returns full-month actuals without a forecast.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Period spend, the currently configured caps, and (current month only) the projected period spend.</response>
    /// <response code="400">The period parameter is not a valid YYYY-MM month.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks access to the client.</response>
    /// <response code="409">The Budgeting capability is not licensed for this installation.</response>
    [HttpGet("consumption")]
    [ProducesResponseType(typeof(ClientBudgetConsumptionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> GetConsumption(Guid clientId, [FromQuery] string? period, CancellationToken ct = default)
    {
        var access = await this.CheckBudgetAccessAsync(clientId, ct);
        if (access is not null)
        {
            return access;
        }

        int? year = null;
        int? month = null;
        if (!string.IsNullOrWhiteSpace(period))
        {
            if (!TryParsePeriod(period, out var parsedYear, out var parsedMonth))
            {
                return this.BadRequest(new { error = "Query parameter 'period' must be a valid month in YYYY-MM format." });
            }

            year = parsedYear;
            month = parsedMonth;
        }

        var consumption = await consumptionService.GetConsumptionAsync(clientId, year, month, ct);
        LogBudgetConsumptionQueried(logger, clientId);
        return this.Ok(consumption);
    }

    /// <summary>Returns the client's estimated USD spend per month over a trailing window, with the currently configured caps.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="months">Number of trailing months to include (default 12; clamped to 1-24).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Per-month spend and the currently configured monthly caps.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks access to the client.</response>
    /// <response code="409">The Budgeting capability is not licensed for this installation.</response>
    [HttpGet("history")]
    [ProducesResponseType(typeof(ClientBudgetHistoryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> GetHistory(Guid clientId, [FromQuery] int? months, CancellationToken ct = default)
    {
        var access = await this.CheckBudgetAccessAsync(clientId, ct);
        if (access is not null)
        {
            return access;
        }

        var history = await consumptionService.GetHistoryAsync(clientId, months ?? DefaultHistoryMonths, ct);
        LogBudgetHistoryQueried(logger, clientId);
        return this.Ok(history);
    }

    /// <summary>Client-admin role check plus the Budgeting license gate; returns a blocking result or null when allowed.</summary>
    private async Task<IActionResult?> CheckBudgetAccessAsync(Guid clientId, CancellationToken ct)
    {
        var roleCheck = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (roleCheck is not null)
        {
            return roleCheck;
        }

        // FinOps surfaces are a licensed capability, like configuring the caps they report against.
        var budgetCapability = await LicensingCapabilityGuard.GetUnavailableCapabilityAsync(
            licensingCapabilityService,
            PremiumCapabilityKey.Budgeting,
            ct);
        return budgetCapability is not null ? new PremiumFeatureUnavailableResult(budgetCapability) : null;
    }

    private static bool TryParsePeriod(string period, out int year, out int month)
    {
        year = 0;
        month = 0;
        var parts = period.Split('-');
        return parts.Length == 2
               && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out year)
               && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out month)
               && year is >= 1 and <= 9999
               && month is >= 1 and <= 12;
    }
}
