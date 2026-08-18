// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json.Serialization;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Dtos;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Services;
using MeisterDev.ProPR.Web;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.Api.Features.UsageStatistics.Controllers;

/// <summary>
///     Administrative endpoints for anonymous usage statistics.
///     <para>
///         Reading the settings and previewing the payload perform no outbound request. Every endpoint here
///         works on local state, so an administrator can inspect what would be sent from an installation that
///         has never sent anything.
///     </para>
/// </summary>
[ApiController]
[Route("admin/usage-statistics")]
public sealed class AdminUsageStatisticsController(UsageStatisticsService? usageStatisticsService = null)
    : ControllerBase
{
    /// <summary>Returns the current setting, the last send outcome, and any update information.</summary>
    /// <param name="ct">Cancels the request.</param>
    /// <response code="200">The current anonymous usage statistics state.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller is not a platform administrator.</response>
    /// <response code="503">The installation has no database, so there is no state to report.</response>
    [HttpGet]
    [ProducesResponseType(typeof(UsageStatisticsSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetUsageStatistics(CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireAdmin(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        if (usageStatisticsService is null)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return this.Ok(await usageStatisticsService.GetSettingsAsync(ct));
    }

    /// <summary>Turns anonymous usage statistics on or off. Refused while a commercial license is installed.</summary>
    /// <param name="request">The requested state.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <response code="200">The updated anonymous usage statistics state.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller is not a platform administrator.</response>
    /// <response code="409">A commercial license governs the setting.</response>
    /// <response code="503">The installation has no database, so there is no state to change.</response>
    [HttpPatch]
    [ProducesResponseType(typeof(UsageStatisticsSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> PatchUsageStatistics(
        [FromBody] PatchAdminUsageStatisticsRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var auth = AuthHelpers.RequireAdmin(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        if (usageStatisticsService is null)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            var updated = await usageStatisticsService.SetCommunityOptInAsync(
                request.Enabled,
                AuthHelpers.GetUserId(this.HttpContext),
                ct);

            return this.Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
    }

    /// <summary>Returns the exact payload the next ping would carry, without sending it.</summary>
    /// <param name="ct">Cancels the request.</param>
    /// <response code="200">The literal request body a ping would post.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller is not a platform administrator.</response>
    /// <response code="503">The installation has no database, so no payload can be built.</response>
    [HttpGet("preview")]
    [ProducesResponseType(typeof(UsageStatisticsPreviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetUsageStatisticsPreview(CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireAdmin(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        if (usageStatisticsService is null)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return this.Ok(await usageStatisticsService.BuildPreviewAsync(ct));
    }

    /// <summary>
    ///     Runs a send cycle now instead of waiting for the daily one.
    ///     <para>
    ///         The rules the background loop applies also apply here: an installation that is switched off or
    ///         has not shown the notice sends nothing, and one that already sent today is reported as not due.
    ///         The response carries which decision was taken.
    ///     </para>
    /// </summary>
    /// <param name="ct">Cancels the request.</param>
    /// <response code="200">What the cycle decided, and the state after it.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller is not a platform administrator.</response>
    /// <response code="503">The installation has no database, so there is nothing to send.</response>
    [HttpPost("send")]
    [ProducesResponseType(typeof(UsageStatisticsSendResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> PostSendNow(CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireAdmin(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        if (usageStatisticsService is null)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            return this.Ok(await usageStatisticsService.SendNowAsync(ct));
        }
        catch (InvalidOperationException)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>
    ///     Records that the consent notice was shown to an administrator, which opens the send gate in a
    ///     community installation. Idempotent.
    /// </summary>
    /// <param name="ct">Cancels the request.</param>
    /// <response code="200">The updated anonymous usage statistics state.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller is not a platform administrator.</response>
    /// <response code="503">The installation has no database, so there is no state to record.</response>
    [HttpPost("notice/shown")]
    [ProducesResponseType(typeof(UsageStatisticsSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> PostNoticeShown(CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireAdmin(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        if (usageStatisticsService is null)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return this.Ok(await usageStatisticsService.RecordNoticeShownAsync(ct));
    }

    /// <summary>Hides the consent notice for this installation. Dismissal does not change what is sent.</summary>
    /// <param name="ct">Cancels the request.</param>
    /// <response code="200">The updated anonymous usage statistics state.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller is not a platform administrator.</response>
    /// <response code="503">The installation has no database, so there is no state to record.</response>
    [HttpPost("notice/dismiss")]
    [ProducesResponseType(typeof(UsageStatisticsSettingsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> PostNoticeDismiss(CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireAdmin(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        if (usageStatisticsService is null)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        return this.Ok(await usageStatisticsService.DismissNoticeAsync(ct));
    }
}

/// <summary>Patch payload for the anonymous usage statistics toggle.</summary>
/// <param name="Enabled">Whether the installation should send a daily snapshot.</param>
public sealed record PatchAdminUsageStatisticsRequest([property: JsonRequired] bool Enabled);
