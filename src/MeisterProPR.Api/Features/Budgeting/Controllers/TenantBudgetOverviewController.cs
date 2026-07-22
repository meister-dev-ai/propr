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

/// <summary>Exposes a tenant-wide view of current-period spend against budget for every client in the tenant.</summary>
[ApiController]
[Route("admin/tenants/{tenantId:guid}/budget")]
public sealed partial class TenantBudgetOverviewController(
    ITenantBudgetOverviewService overviewService,
    ILogger<TenantBudgetOverviewController> logger,
    ILicensingCapabilityService? licensingCapabilityService = null) : ControllerBase
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Tenant budget overview queried for tenant {TenantId}")]
    private static partial void LogTenantBudgetOverviewQueried(ILogger logger, Guid tenantId);

    /// <summary>Returns current-period spend against budget for every client in the tenant.</summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Per-client spend, configured caps, and projected period spend for the current period.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller is not an administrator of this tenant.</response>
    /// <response code="409">The Budgeting capability is not licensed for this installation.</response>
    [HttpGet("overview")]
    [ProducesResponseType(typeof(TenantBudgetOverviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> GetOverview(Guid tenantId, CancellationToken ct = default)
    {
        var roleCheck = AuthHelpers.RequireTenantRole(this.HttpContext, tenantId, TenantRole.TenantAdministrator);
        if (roleCheck is not null)
        {
            return roleCheck;
        }

        // FinOps surfaces are a licensed capability, like configuring the caps they report against.
        var budgetCapability = await LicensingCapabilityGuard.GetUnavailableCapabilityAsync(
            licensingCapabilityService,
            PremiumCapabilityKey.Budgeting,
            ct);
        if (budgetCapability is not null)
        {
            return new PremiumFeatureUnavailableResult(budgetCapability);
        }

        var overview = await overviewService.GetOverviewAsync(tenantId, ct);
        LogTenantBudgetOverviewQueried(logger, tenantId);
        return this.Ok(overview);
    }
}
