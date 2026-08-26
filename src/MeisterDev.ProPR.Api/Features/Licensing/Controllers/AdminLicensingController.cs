// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.Features.Licensing.Commands.ActivateLicense;
using MeisterDev.ProPR.Application.Features.Licensing.Commands.RemoveLicense;
using MeisterDev.ProPR.Application.Features.Licensing.Commands.UpdateLicensing;
using MeisterDev.ProPR.Application.Features.Licensing.Dtos;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Queries.GetLicenseActivationHistory;
using MeisterDev.ProPR.Application.Features.Licensing.Queries.GetLicensingSummary;
using MeisterDev.ProPR.Application.Features.Licensing.Queries.GetSystemProfile;
using MeisterDev.ProPR.Licensing;
using Microsoft.AspNetCore.Mvc;
using MeisterDev.ProPR.Web;

namespace MeisterDev.ProPR.Api.Features.Licensing.Controllers;

/// <summary>Administrative endpoints for the installation's license and premium capability state.</summary>
[ApiController]
[Route("admin/licensing")]
public sealed class AdminLicensingController(
    GetLicensingSummaryHandler? getLicensingSummaryHandler = null,
    UpdateLicensingHandler? updateLicensingHandler = null,
    ActivateLicenseHandler? activateLicenseHandler = null,
    RemoveLicenseHandler? removeLicenseHandler = null,
    GetLicenseActivationHistoryHandler? getLicenseActivationHistoryHandler = null,
    GetSystemProfileHandler? getSystemProfileHandler = null) : ControllerBase
{
    /// <summary>The stable code that names an activation refusal in the response body.</summary>
    private const string LicenseNotAcceptedError = "license_not_accepted";

    /// <summary>
    ///     Returns the current installation edition, premium capability state, and every quantitative limit
    ///     with the ceiling the installation is held to beside what the license states and what the
    ///     installation currently holds. The effective ceiling is the number an enforcement refusal quotes.
    /// </summary>
    /// <param name="ct">Cancels the request.</param>
    /// <response code="200">The current licensing summary.</response>
    /// <response code="401">No valid credentials were supplied.</response>
    /// <response code="403">The caller is not an administrator.</response>
    /// <response code="503">Licensing is not available on this deployment.</response>
    [HttpGet]
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

    /// <summary>
    ///     Activates a license document, replacing the one on file. The document is verified before it is
    ///     stored, so a refused request leaves the previous license in place.
    /// </summary>
    /// <param name="request">The license document.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <response code="200">The licensing summary the installation now reports.</response>
    /// <response code="204">The license was stored and the summary could not be read back.</response>
    /// <response code="400">The document was refused; the body names the reason.</response>
    /// <response code="401">No valid credentials were supplied.</response>
    /// <response code="403">The caller is not an administrator.</response>
    /// <response code="503">Licensing is not available on this deployment.</response>
    [HttpPut("license")]
    [ProducesResponseType(typeof(LicensingSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(LicenseActivationRefusedPayload), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ActivateLicense(
        [FromBody] ActivateLicenseRequest request,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireAdmin(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        if (activateLicenseHandler is null)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var result = await activateLicenseHandler.HandleAsync(
            new ActivateLicenseCommand(request.Token, AuthHelpers.GetUserId(this.HttpContext)),
            ct);

        // The summary is omitted when it could not be read back, which does not change that the license was
        // stored. That case answers 204, so every 200 carries the summary its declared type promises and a
        // generated client never has to parse an empty body. A caller that gets 204 reads the licensing summary
        // again rather than treating the activation as failed.
        return result.IsActivated
            ? result.Summary is null ? this.NoContent() : this.Ok(result.Summary)
            : this.BadRequest(
                new LicenseActivationRefusedPayload(
                    LicenseNotAcceptedError,
                    result.RefusalReason.Value,
                    result.RefusalDetail));
    }

    /// <summary>
    ///     Removes the license on file. Removing when none is on file is not an error and records nothing.
    /// </summary>
    /// <param name="ct">Cancels the request.</param>
    /// <response code="204">No license is on file.</response>
    /// <response code="401">No valid credentials were supplied.</response>
    /// <response code="403">The caller is not an administrator.</response>
    /// <response code="503">Licensing is not available on this deployment.</response>
    [HttpDelete("license")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> RemoveLicense(CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireAdmin(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        if (removeLicenseHandler is null)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        await removeLicenseHandler.HandleAsync(new RemoveLicenseCommand(AuthHelpers.GetUserId(this.HttpContext)), ct);

        return this.NoContent();
    }

    /// <summary>
    ///     Returns the recorded license activations, replacements and removals, newest first, at most the 200
    ///     most recent records. The records survive the removal of the license they describe.
    /// </summary>
    /// <param name="ct">Cancels the request.</param>
    /// <response code="200">The recorded license changes.</response>
    /// <response code="401">No valid credentials were supplied.</response>
    /// <response code="403">The caller is not an administrator.</response>
    /// <response code="503">Licensing is not available on this deployment.</response>
    [HttpGet("history")]
    [ProducesResponseType(typeof(IReadOnlyList<LicenseActivationEventDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetLicenseHistory(CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireAdmin(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        if (getLicenseActivationHistoryHandler is null)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return this.Ok(await getLicenseActivationHistoryHandler.HandleAsync(new GetLicenseActivationHistoryQuery(), ct));
    }

    /// <summary>
    ///     Returns what the installation has observed about the system it runs on: the profile it currently
    ///     reports, the recorded changes to that profile newest first, and the host names it has been seen
    ///     running on. The profile is descriptive; no license check reads it.
    /// </summary>
    /// <param name="ct">Cancels the request.</param>
    /// <response code="200">The observed system profile.</response>
    /// <response code="401">No valid credentials were supplied.</response>
    /// <response code="403">The caller is not an administrator.</response>
    /// <response code="503">Licensing is not available on this deployment.</response>
    [HttpGet("profile")]
    [ProducesResponseType(typeof(SystemProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetSystemProfile(CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireAdmin(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        if (getSystemProfileHandler is null)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return this.Ok(await getSystemProfileHandler.HandleAsync(new GetSystemProfileQuery(), ct));
    }

    /// <summary>
    ///     Updates the per-capability overrides the installation applies on top of its license. An override can
    ///     only take a capability away, so <c>overrideState</c> carries either <c>default</c> or <c>disabled</c>.
    /// </summary>
    /// <param name="request">The override mutations to apply.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <response code="200">The licensing summary the installation now reports.</response>
    /// <response code="400">
    ///     An override carries no capability key, names a capability that does not exist, or carries a state the
    ///     endpoint does not accept.
    /// </response>
    /// <response code="401">No valid credentials were supplied.</response>
    /// <response code="403">The caller is not an administrator.</response>
    /// <response code="503">Licensing is not available on this deployment.</response>
    [HttpPatch("overrides")]
    [ProducesResponseType(typeof(LicensingSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> PatchLicensingOverrides(
        [FromBody] PatchLicensingOverridesRequest request,
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

        var overrides = request.CapabilityOverrides ?? [];

        // A key that is empty or only whitespace is refused here rather than below, because the catalog lookup
        // treats it as a caller error and throws a different exception than an unknown key does. Both are the
        // same mistake to an operator, so both answer 400 naming what is wrong. Binding covers a key that is
        // null or absent.
        if (overrides.Any(overrideRequest => string.IsNullOrWhiteSpace(overrideRequest.Key)))
        {
            return this.BadRequest(new { error = "A capability override must name a capability key." });
        }

        try
        {
            var updated = await updateLicensingHandler.HandleAsync(
                new UpdateLicensingCommand(
                    overrides
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
    }
}

/// <summary>Payload carrying the license document to activate.</summary>
/// <param name="Token">
///     The compact license document. Text pasted by an operator and the contents of a file the browser read are
///     the same value here.
/// </param>
public sealed record ActivateLicenseRequest([property: JsonRequired] string Token);

/// <summary>Body naming why an activation was refused.</summary>
/// <param name="Error">The stable code for an activation refusal.</param>
/// <param name="Reason">Which of the typed refusal reasons applies.</param>
/// <param name="Message">What an operator has to change about the document.</param>
public sealed record LicenseActivationRefusedPayload(
    string Error,
    LicenseFailureReason Reason,
    string Message);

/// <summary>Patch payload for the installation's premium capability overrides.</summary>
/// <param name="CapabilityOverrides">The overrides to apply. Omitting it changes nothing.</param>
public sealed record PatchLicensingOverridesRequest(IReadOnlyList<PatchPremiumCapabilityOverrideRequest>? CapabilityOverrides);

/// <summary>Patch payload for one premium capability override.</summary>
/// <param name="Key">The capability key.</param>
/// <param name="OverrideState">The state to apply to it: <c>default</c> to leave it to the license, or <c>disabled</c>.</param>
public sealed record PatchPremiumCapabilityOverrideRequest(
    string Key,
    PremiumCapabilityOverrideState OverrideState);
