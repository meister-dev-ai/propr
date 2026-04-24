// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.Features.Licensing.Commands.UpdateLicensing;
using MeisterProPR.Application.Features.Licensing.Dtos;
using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Queries.GetLicensingSummary;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Features.Licensing.Controllers;

/// <summary>Administrative endpoints for installation-wide edition and premium capability state.</summary>
[ApiController]
public sealed class AdminLicensingController(
    GetLicensingSummaryHandler? getLicensingSummaryHandler = null,
    UpdateLicensingHandler? updateLicensingHandler = null) : ControllerBase
{
    /// <summary>Returns the current installation edition and premium capability state.</summary>
    [HttpGet("/admin/licensing")]
    [ProducesResponseType(typeof(LicensingSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetLicensing(CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireAdmin(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        if (getLicensingSummaryHandler is null)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return this.Ok(await getLicensingSummaryHandler.HandleAsync(new GetLicensingSummaryQuery(), ct));
    }

    /// <summary>Updates the installation edition and optional per-capability overrides.</summary>
    [HttpPatch("/admin/licensing")]
    [ProducesResponseType(typeof(LicensingSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> PatchLicensing(
        [FromBody] PatchAdminLicensingRequest request,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireAdmin(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        if (updateLicensingHandler is null)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            var updated = await updateLicensingHandler.HandleAsync(
                new UpdateLicensingCommand(
                    request.Edition,
                    (request.CapabilityOverrides ?? [])
                        .Select(overrideRequest => new CapabilityOverrideMutation(
                            overrideRequest.Key,
                            overrideRequest.OverrideState))
                        .ToArray(),
                    AuthHelpers.GetUserId(this.HttpContext)),
                ct);

            return this.Ok(updated);
        }
        catch (KeyNotFoundException ex)
        {
            return this.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
    }
}

/// <summary>Patch payload for installation licensing updates.</summary>
public sealed record PatchAdminLicensingRequest(
    InstallationEdition Edition,
    IReadOnlyList<PatchPremiumCapabilityOverrideRequest>? CapabilityOverrides);

/// <summary>Patch payload for one premium capability override.</summary>
public sealed record PatchPremiumCapabilityOverrideRequest(
    string Key,
    PremiumCapabilityOverrideState OverrideState);
