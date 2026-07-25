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
    IClientBudgetResetService resetService,
    ILogger<ClientBudgetConsumptionController> logger,
    ILicensingCapabilityService? licensingCapabilityService = null) : ControllerBase
{
    private const int DefaultHistoryMonths = 12;

    [LoggerMessage(Level = LogLevel.Information, Message = "Budget consumption queried for client {ClientId}")]
    private static partial void LogBudgetConsumptionQueried(ILogger logger, Guid clientId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Budget history queried for client {ClientId}")]
    private static partial void LogBudgetHistoryQueried(ILogger logger, Guid clientId);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Manual spend reset applied for client {ClientId} in period {PeriodStart} by actor {ActorUserId}")]
    private static partial void LogSpendResetApplied(ILogger logger, Guid clientId, DateOnly periodStart, Guid? actorUserId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Manual spend reset refused for client {ClientId}: {Outcome}")]
    private static partial void LogSpendResetRefused(ILogger logger, Guid clientId, BudgetSpendResetOutcome outcome);

    /// <summary>Returns the client's monthly budget consumption and forecast for a period (the current month by default).</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="period">Optional target month as <c>YYYY-MM</c>; omit for the current month. A past month returns full-month actuals without a forecast.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Period spend, the caps in force for the period (configured plus any manual-reset allowance), and (current month only) the projected period spend.</response>
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

    /// <summary>
    ///     Grants the client's current monthly period a fresh allowance on top of what it has already consumed. Spend
    ///     to date is preserved; the period's effective cap becomes the cap in force before the reset plus the
    ///     configured cap. The reset is recorded with its actor for audit.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The recorded reset, including the effective caps before and after it.</response>
    /// <response code="400">The client has no monthly budget cap configured, so there is no ceiling to raise.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks access to the client.</response>
    /// <response code="404">No such client.</response>
    /// <response code="409">The Budgeting capability is not licensed for this installation.</response>
    [HttpPost("reset")]
    [ProducesResponseType(typeof(BudgetSpendResetDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ResetSpend(Guid clientId, CancellationToken ct = default)
    {
        var access = await this.CheckBudgetAccessAsync(clientId, ct);
        if (access is not null)
        {
            return access;
        }

        // A reset raises what a client may spend, so it is only recorded against a named administrator. An
        // unresolvable caller would leave an audit row nobody can be held to.
        if (AuthHelpers.GetUserId(this.HttpContext) is not { } actorUserId)
        {
            return this.Unauthorized(new { error = "The acting administrator could not be identified." });
        }

        var result = await resetService.ResetAsync(clientId, actorUserId, ct);

        if (result.Outcome is not BudgetSpendResetOutcome.Applied || result.Reset is null)
        {
            LogSpendResetRefused(logger, clientId, result.Outcome);
            return result.Outcome switch
            {
                BudgetSpendResetOutcome.ClientNotFound => this.NotFound(),
                BudgetSpendResetOutcome.NoMonthlyCapConfigured => this.BadRequest(
                    new
                    {
                        error = "The client has no monthly budget cap configured, so there is no allowance to top up.",
                    }),
                _ => this.BadRequest(new { error = "The spend reset could not be applied." }),
            };
        }

        var reset = result.Reset;
        LogSpendResetApplied(logger, clientId, reset.PeriodStart, reset.ActorUserId);
        return this.Ok(
            new BudgetSpendResetDto(
                reset.Id,
                reset.PeriodStart,
                reset.TopUpSoftCapUsd,
                reset.TopUpHardCapUsd,
                reset.EffectiveSoftCapBeforeUsd,
                reset.EffectiveSoftCapAfterUsd,
                reset.EffectiveHardCapBeforeUsd,
                reset.EffectiveHardCapAfterUsd,
                reset.ActorUserId,
                ActorUsername: null,
                reset.PerformedAt));
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
