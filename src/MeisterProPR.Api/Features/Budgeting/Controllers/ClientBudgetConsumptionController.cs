// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

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

/// <summary>Exposes a client's USD spend against its monthly budget, with a trajectory projection.</summary>
[ApiController]
[Route("admin/clients/{clientId:guid}/budget")]
public sealed partial class ClientBudgetConsumptionController(
    IClientBudgetConsumptionService consumptionService,
    ILogger<ClientBudgetConsumptionController> logger,
    ILicensingCapabilityService? licensingCapabilityService = null) : ControllerBase
{
    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Budget consumption queried for client {ClientId}")]
    private static partial void LogBudgetConsumptionQueried(ILogger logger, Guid clientId);

    /// <summary>Returns the client's monthly budget consumption and forecast for the current period.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Current-period spend, configured caps, and the projected period spend.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks access to the client.</response>
    /// <response code="409">The Budgeting capability is not licensed for this installation.</response>
    [HttpGet("consumption")]
    [ProducesResponseType(typeof(ClientBudgetConsumptionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> GetConsumption(Guid clientId, CancellationToken ct = default)
    {
        var roleCheck = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (roleCheck is not null)
        {
            return roleCheck;
        }

        // FinOps consumption is a licensed capability, like configuring the caps it reports against.
        var budgetCapability = await LicensingCapabilityGuard.GetUnavailableCapabilityAsync(
            licensingCapabilityService,
            PremiumCapabilityKey.Budgeting,
            ct);
        if (budgetCapability is not null)
        {
            return new PremiumFeatureUnavailableResult(budgetCapability);
        }

        var consumption = await consumptionService.GetConsumptionAsync(clientId, ct);
        LogBudgetConsumptionQueried(logger, clientId);
        return this.Ok(consumption);
    }
}
