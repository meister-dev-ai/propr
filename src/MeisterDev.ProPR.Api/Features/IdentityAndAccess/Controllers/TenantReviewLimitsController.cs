// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.ComponentModel.DataAnnotations;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Web;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>The limits on how the reviews of a tenant run. A null value clears that limit.</summary>
/// <param name="MaxConcurrentReviews">
///     How many review jobs of the tenant may be processing at once, in process and on runners together, at least
///     1, or null for no limit of the tenant.
/// </param>
/// <param name="MaxReviewDurationSeconds">
///     How many seconds after the start of processing a review of the tenant ends with what it has reviewed, from 60
///     to 86400, or null for no limit of the tenant.
/// </param>
public sealed record TenantReviewLimitsRequest(
    [Range(
        TenantReviewExecutionLimits.MinimumConcurrentReviews,
        int.MaxValue,
        ErrorMessage = "maxConcurrentReviews must be at least 1, or null for no limit.")]
    int? MaxConcurrentReviews,
    [Range(
        TenantReviewExecutionLimits.MinimumReviewDurationSeconds,
        TenantReviewExecutionLimits.MaximumReviewDurationSeconds,
        ErrorMessage = "maxReviewDurationSeconds must be from 60 to 86400, or null for no limit.")]
    int? MaxReviewDurationSeconds);

/// <summary>
///     Reads and sets the limits on how the reviews of a tenant run: how many of its review jobs may be processing at
///     once, and how long one review may run. Platform administrators only.
/// </summary>
[ApiController]
[Route("admin/tenants/{tenantId:guid}")]
public sealed class TenantReviewLimitsController(ITenantReviewExecutionLimitsStore limits) : ControllerBase
{
    /// <summary>Returns the review limits of the tenant. A null value means that the tenant sets no such limit.</summary>
    /// <response code="200">The limits of the tenant.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">The caller is not a platform administrator.</response>
    /// <response code="404">The tenant does not exist.</response>
    [TenantData(TenantDataAccess.Metadata)]
    [HttpGet("review-limits")]
    [ProducesResponseType(typeof(TenantReviewExecutionLimits), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetReviewLimits(Guid tenantId, CancellationToken ct)
    {
        if (AuthHelpers.RequirePlatformAdmin(this.HttpContext) is { } refused)
        {
            return refused;
        }

        var stored = await limits.GetAsync(tenantId, ct);
        return stored is null ? this.NotFound() : this.Ok(stored);
    }

    /// <summary>
    ///     Stores both review limits of the tenant. A null value clears that limit, so a request that leaves out a
    ///     value clears it as well. Sending the stored values again changes nothing. A new cap on concurrent reviews
    ///     applies to the next claim and stops no review that is running. A new maximum review duration applies to
    ///     reviews that start their scan after the change.
    /// </summary>
    /// <response code="200">The limits were stored; the answer contains the stored values.</response>
    /// <response code="400">
    ///     <c>maxConcurrentReviews</c> is below 1, or <c>maxReviewDurationSeconds</c> is outside 60 to 86400.
    /// </response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">The caller is not a platform administrator.</response>
    /// <response code="404">The tenant does not exist.</response>
    [TenantData(TenantDataAccess.Metadata)]
    [HttpPut("review-limits")]
    [ProducesResponseType(typeof(TenantReviewExecutionLimits), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetReviewLimits(
        Guid tenantId,
        [FromBody] TenantReviewLimitsRequest request,
        CancellationToken ct)
    {
        if (AuthHelpers.RequirePlatformAdmin(this.HttpContext) is { } refused)
        {
            return refused;
        }

        var stored = await limits.SetAsync(
            tenantId,
            new TenantReviewExecutionLimits(request.MaxConcurrentReviews, request.MaxReviewDurationSeconds),
            AuthHelpers.GetUserId(this.HttpContext),
            ct);
        return stored is null ? this.NotFound() : this.Ok(stored);
    }
}
