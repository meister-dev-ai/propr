// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Api.Features.Licensing;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Licensing.Support;
using MeisterDev.ProPR.Application.Features.TenantAudit;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Web;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>
///     The audit history of a tenant, for its tenant administrators and for platform administrators. The history is
///     part of the multi-tenancy capability, like the elevations it records.
/// </summary>
[ApiController]
[Route("admin/tenants/{tenantId:guid}/audit-entries")]
[TenantData(TenantDataAccess.Metadata)]
public sealed class TenantAuditEntriesController(
    ITenantAuditHistoryReader reader,
    ILicensingCapabilityService? licensingCapabilityService = null) : ControllerBase
{
    /// <summary>
    ///     Returns one page of the audit history of the tenant, the newest entry first. The history contains changes to
    ///     the tenant's settings, memberships, sign-in providers and machine credentials, identity erasures, and the
    ///     elevations of platform administrators with every content read under them.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="page">The page, starting at 1.</param>
    /// <param name="pageSize">The entries per page, from 1 to 200.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">One page of the audit history.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">The caller administers neither this tenant nor the installation.</response>
    /// <response code="404">The tenant does not exist.</response>
    /// <response code="409">The license of the installation does not include multi-tenancy.</response>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<TenantAuditEntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PremiumFeatureUnavailablePayload), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> List(
        Guid tenantId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        if (AuthHelpers.RequireTenantRole(this.HttpContext, tenantId, TenantRole.TenantAdministrator) is { } refused)
        {
            return refused;
        }

        var capability = await LicensingCapabilityGuard.GetUnavailableCapabilityAsync(
            licensingCapabilityService,
            PremiumCapabilityKey.MultiTenancy,
            ct);
        if (capability is not null)
        {
            return new PremiumFeatureUnavailableResult(capability);
        }

        var history = await reader.GetAsync(tenantId, page, pageSize, ct);
        return history is null ? this.NotFound() : this.Ok(history);
    }
}
