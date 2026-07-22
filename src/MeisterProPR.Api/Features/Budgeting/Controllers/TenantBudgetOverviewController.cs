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

/// <summary>Exposes tenant-wide FinOps surfaces: the per-client budget overview and the aggregate spend view.</summary>
[ApiController]
[Route("admin/tenants/{tenantId:guid}/budget")]
public sealed partial class TenantBudgetOverviewController(
    ITenantBudgetOverviewService overviewService,
    ITenantBudgetSpendService spendService,
    ILogger<TenantBudgetOverviewController> logger,
    ILicensingCapabilityService? licensingCapabilityService = null) : ControllerBase
{
    private const int DefaultHistoryMonths = 12;

    [LoggerMessage(Level = LogLevel.Information, Message = "Tenant budget overview queried for tenant {TenantId}")]
    private static partial void LogTenantBudgetOverviewQueried(ILogger logger, Guid tenantId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tenant budget spend queried for tenant {TenantId}")]
    private static partial void LogTenantBudgetSpendQueried(ILogger logger, Guid tenantId);

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
        var access = await this.CheckTenantBudgetAccessAsync(tenantId, ct);
        if (access is not null)
        {
            return access;
        }

        var overview = await overviewService.GetOverviewAsync(tenantId, ct);
        LogTenantBudgetOverviewQueried(logger, tenantId);
        return this.Ok(overview);
    }

    /// <summary>Returns the tenant's aggregate current-period spend and a trailing per-month trend.</summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="months">Number of trailing months to include in the trend (default 12; clamped to 1-24).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Aggregate spend, summed client caps, projected period spend, and the per-month trend.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller is not an administrator of this tenant.</response>
    /// <response code="409">The Budgeting capability is not licensed for this installation.</response>
    [HttpGet("spend")]
    [ProducesResponseType(typeof(TenantSpendDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> GetSpend(Guid tenantId, [FromQuery] int? months, CancellationToken ct = default)
    {
        var access = await this.CheckTenantBudgetAccessAsync(tenantId, ct);
        if (access is not null)
        {
            return access;
        }

        var spend = await spendService.GetSpendAsync(tenantId, months ?? DefaultHistoryMonths, ct);
        LogTenantBudgetSpendQueried(logger, tenantId);
        return this.Ok(spend);
    }

    /// <summary>Tenant-admin role check plus the Budgeting license gate; returns a blocking result or null when allowed.</summary>
    private async Task<IActionResult?> CheckTenantBudgetAccessAsync(Guid tenantId, CancellationToken ct)
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
        return budgetCapability is not null ? new PremiumFeatureUnavailableResult(budgetCapability) : null;
    }
}
