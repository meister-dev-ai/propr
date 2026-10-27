// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.ComponentModel.DataAnnotations;
using MeisterDev.ProPR.Api.Features.Licensing;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Licensing.Support;
using MeisterDev.ProPR.Application.Features.PlatformAdminElevation;
using MeisterDev.ProPR.Application.Features.TenantAudit;
using MeisterDev.ProPR.Web;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>A request of a platform administrator to start an elevation for themselves.</summary>
/// <param name="TenantId">The tenant whose content the elevation covers.</param>
/// <param name="ClientId">
///     One client of the tenant, to limit the elevation to that client; null covers every client of the tenant. An empty
///     identifier is refused.
/// </param>
/// <param name="Reason">Why the administrator needs the content, for example a ticket number. Recorded with every read.</param>
/// <param name="DurationMinutes">How long the elevation lasts, from 1 minute to the maximum of the installation.</param>
public sealed record StartPlatformAdminElevationRequest(
    Guid TenantId,
    Guid? ClientId,
    [Required, MaxLength(PlatformAdminElevationLimits.MaxReasonLength)]
    string Reason,
    [Range(1, 1440)] int DurationMinutes);

/// <summary>The elevations of the calling platform administrator, with the bound of the installation.</summary>
/// <param name="MaxDurationMinutes">The longest duration, in minutes, the installation permits for one elevation.</param>
/// <param name="Active">The active elevations of the caller, the one that expires first first.</param>
public sealed record PlatformAdminElevationStatusResponse(int MaxDurationMinutes, IReadOnlyList<PlatformAdminElevationDto> Active);

/// <summary>
///     Starts, ends and lists the elevations with which platform administrators read the content of a tenant. Platform
///     administrators only.
/// </summary>
/// <remarks>
///     A platform administrator reads metadata of every tenant without an elevation. Content of a client of a tenant other
///     than the System tenant needs an elevation that covers the client. ProPR records the start, the end and every
///     content read of an elevation in the audit history of the tenant, which the tenant administrators can read.
///     Elevations are part of the multi-tenancy capability.
/// </remarks>
[ApiController]
[Route("admin/elevations")]
[TenantData(TenantDataAccess.Metadata)]
public sealed class PlatformAdminElevationsController(
    IPlatformAdminElevationService elevations,
    ILicensingCapabilityService? licensingCapabilityService = null) : ControllerBase
{
    /// <summary>
    ///     Returns 409 on every action here when the license of the installation does not include multi-tenancy. An
    ///     elevation covers a tenant other than the System tenant, and an installation without multi-tenancy uses the
    ///     System tenant only, whose content needs no elevation.
    /// </summary>
    private async Task<IActionResult?> RequireMultiTenancyCapabilityAsync(CancellationToken ct)
    {
        var capability = await LicensingCapabilityGuard.GetUnavailableCapabilityAsync(
            licensingCapabilityService,
            PremiumCapabilityKey.MultiTenancy,
            ct);

        return capability is null ? null : new PremiumFeatureUnavailableResult(capability);
    }

    /// <summary>Returns the active elevations of the caller and the longest duration the installation permits.</summary>
    /// <response code="200">The active elevations of the caller.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">The caller is not a platform administrator.</response>
    /// <response code="409">The license of the installation does not include multi-tenancy.</response>
    [HttpGet("active")]
    [ProducesResponseType(typeof(PlatformAdminElevationStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(PremiumFeatureUnavailablePayload), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> GetActive(CancellationToken ct = default)
    {
        if (AuthHelpers.RequirePlatformAdmin(this.HttpContext) is { } refused)
        {
            return refused;
        }

        if (await this.RequireMultiTenancyCapabilityAsync(ct) is { } unlicensed)
        {
            return unlicensed;
        }

        var active = AuthHelpers.GetUserId(this.HttpContext) is { } userId
            ? await elevations.GetActiveAsync(userId, ct)
            : [];
        return this.Ok(new PlatformAdminElevationStatusResponse(elevations.MaxDurationMinutes, active));
    }

    /// <summary>
    ///     Starts an elevation of the caller for one tenant, or for one client of a tenant. The elevation grants read
    ///     access to the content of its scope until it expires or is ended. It grants no write access.
    /// </summary>
    /// <response code="201">The elevation started.</response>
    /// <response code="400">
    ///     The reason is missing or too long, the duration is outside the permitted range, the client identifier is empty,
    ///     or the tenant is the System tenant.
    /// </response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">The caller is not a platform administrator.</response>
    /// <response code="404">The tenant does not exist, or the client does not belong to it.</response>
    /// <response code="409">The license of the installation does not include multi-tenancy.</response>
    [HttpPost]
    [ProducesResponseType(typeof(PlatformAdminElevationDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PremiumFeatureUnavailablePayload), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Start([FromBody] StartPlatformAdminElevationRequest request, CancellationToken ct = default)
    {
        if (AuthHelpers.RequirePlatformAdmin(this.HttpContext) is { } refused)
        {
            return refused;
        }

        if (await this.RequireMultiTenancyCapabilityAsync(ct) is { } unlicensed)
        {
            return unlicensed;
        }

        if (AuthHelpers.GetUserId(this.HttpContext) is not { } userId)
        {
            return this.StatusCode(StatusCodes.Status403Forbidden, new { error = "An elevation needs a platform administrator account." });
        }

        // Only a missing client covers every client of the tenant. An empty identifier is refused, so a client that
        // could not be read never widens the scope to the whole tenant.
        if (request.ClientId == Guid.Empty)
        {
            this.ModelState.AddModelError(
                nameof(request.ClientId),
                "Name a client of the tenant, or leave out clientId to cover every client of the tenant.");
            return this.ValidationProblem();
        }

        var result = await elevations.StartAsync(
            userId,
            request.TenantId,
            request.ClientId,
            request.Reason,
            request.DurationMinutes,
            ct);

        switch (result.Outcome)
        {
            case StartElevationOutcome.Started:
                return this.CreatedAtAction(nameof(this.Get), new { elevationId = result.Elevation!.Id }, result.Elevation);
            case StartElevationOutcome.TenantNotFound:
                return this.NotFound(new { error = "The tenant does not exist." });
            case StartElevationOutcome.ClientNotFound:
                return this.NotFound(new { error = "The client does not exist in this tenant." });
            case StartElevationOutcome.SystemTenant:
                this.ModelState.AddModelError(
                    nameof(request.TenantId),
                    "The System tenant belongs to the installation. Platform administrators read its content without an elevation.");
                return this.ValidationProblem();
            case StartElevationOutcome.InvalidDuration:
                this.ModelState.AddModelError(
                    nameof(request.DurationMinutes),
                    $"The duration must be between 1 and {elevations.MaxDurationMinutes} minutes.");
                return this.ValidationProblem();
            case StartElevationOutcome.InvalidReason:
                this.ModelState.AddModelError(
                    nameof(request.Reason),
                    $"Enter a reason of at most {PlatformAdminElevationLimits.MaxReasonLength} characters, for example a ticket number.");
                return this.ValidationProblem();
            case StartElevationOutcome.NotPlatformAdministrator:
            default:
                return this.StatusCode(StatusCodes.Status403Forbidden, new { error = "An elevation needs an active platform administrator account." });
        }
    }

    /// <summary>Ends an active elevation before its expiry. Any platform administrator can end any elevation.</summary>
    /// <response code="200">The elevation ended.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">The caller is not a platform administrator.</response>
    /// <response code="404">The elevation does not exist.</response>
    /// <response code="409">
    ///     The elevation had already ended or expired, or the license of the installation does not include
    ///     multi-tenancy.
    /// </response>
    [HttpPost("{elevationId:guid}/end")]
    [ProducesResponseType(typeof(PlatformAdminElevationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> End(Guid elevationId, CancellationToken ct = default)
    {
        if (AuthHelpers.RequirePlatformAdmin(this.HttpContext) is { } refused)
        {
            return refused;
        }

        if (await this.RequireMultiTenancyCapabilityAsync(ct) is { } unlicensed)
        {
            return unlicensed;
        }

        if (AuthHelpers.GetUserId(this.HttpContext) is not { } userId)
        {
            return this.StatusCode(StatusCodes.Status403Forbidden, new { error = "Ending an elevation needs a platform administrator account." });
        }

        var result = await elevations.EndAsync(elevationId, userId, ct);
        return result.Outcome switch
        {
            EndElevationOutcome.Ended => this.Ok(result.Elevation),
            EndElevationOutcome.NotFound => this.NotFound(),
            _ => this.Conflict(new { error = "The elevation has already ended or expired." }),
        };
    }

    /// <summary>Lists the elevations of all platform administrators, the newest first.</summary>
    /// <param name="tenantId">Limits the list to the elevations of one tenant.</param>
    /// <param name="page">The page, starting at 1.</param>
    /// <param name="pageSize">The elevations per page, from 1 to 200.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">One page of elevations.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">The caller is not a platform administrator.</response>
    /// <response code="409">The license of the installation does not include multi-tenancy.</response>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<PlatformAdminElevationDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(PremiumFeatureUnavailablePayload), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> List(
        [FromQuery] Guid? tenantId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        if (AuthHelpers.RequirePlatformAdmin(this.HttpContext) is { } refused)
        {
            return refused;
        }

        if (await this.RequireMultiTenancyCapabilityAsync(ct) is { } unlicensed)
        {
            return unlicensed;
        }

        return this.Ok(await elevations.ListAsync(tenantId, page, pageSize, ct));
    }

    /// <summary>Returns one elevation.</summary>
    /// <response code="200">The elevation.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">The caller is not a platform administrator.</response>
    /// <response code="404">The elevation does not exist.</response>
    /// <response code="409">The license of the installation does not include multi-tenancy.</response>
    [HttpGet("{elevationId:guid}")]
    [ProducesResponseType(typeof(PlatformAdminElevationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PremiumFeatureUnavailablePayload), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Get(Guid elevationId, CancellationToken ct = default)
    {
        if (AuthHelpers.RequirePlatformAdmin(this.HttpContext) is { } refused)
        {
            return refused;
        }

        if (await this.RequireMultiTenancyCapabilityAsync(ct) is { } unlicensed)
        {
            return unlicensed;
        }

        var elevation = await elevations.GetAsync(elevationId, ct);
        return elevation is null ? this.NotFound() : this.Ok(elevation);
    }

    /// <summary>
    ///     Returns the audit entries of one elevation, the oldest first: its start, every content read and its end or
    ///     expiry. The same entries are in the audit history of the tenant.
    /// </summary>
    /// <response code="200">The audit entries of the elevation.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">The caller is not a platform administrator.</response>
    /// <response code="404">The elevation does not exist.</response>
    /// <response code="409">The license of the installation does not include multi-tenancy.</response>
    [HttpGet("{elevationId:guid}/audit-entries")]
    [ProducesResponseType(typeof(IReadOnlyList<TenantAuditEntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PremiumFeatureUnavailablePayload), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> GetAuditEntries(Guid elevationId, CancellationToken ct = default)
    {
        if (AuthHelpers.RequirePlatformAdmin(this.HttpContext) is { } refused)
        {
            return refused;
        }

        if (await this.RequireMultiTenancyCapabilityAsync(ct) is { } unlicensed)
        {
            return unlicensed;
        }

        if (await elevations.GetAsync(elevationId, ct) is null)
        {
            return this.NotFound();
        }

        return this.Ok(await elevations.GetAuditEntriesAsync(elevationId, ct));
    }
}
