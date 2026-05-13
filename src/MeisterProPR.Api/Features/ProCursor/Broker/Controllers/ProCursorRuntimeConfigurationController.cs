// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Features.ProCursor.Broker.Services;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Infrastructure.Features.ProCursor.Remote;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Features.ProCursor.Broker.Controllers;

/// <summary>
///     Internal runtime-configuration endpoints used by the extracted ProCursor runtime.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = ProCursorSharedKeyAuthenticationDefaults.Scheme)]
public sealed class ProCursorRuntimeConfigurationController(ProCursorRuntimeConfigurationProjectionService projectionService) : ControllerBase
{
    /// <summary>
    ///     Lists runtime-configuration projections that are enabled for ProCursor.
    /// </summary>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <returns>The enabled runtime-configuration projections.</returns>
    /// <response code="200">Enabled projections were returned.</response>
    [HttpGet("/internal/propr/procursor/runtime-config/enabled")]
    public async Task<IActionResult> ListEnabled(CancellationToken ct)
    {
        return this.Ok(await projectionService.ListEnabledAsync(ct));
    }

    /// <summary>
    ///     Refreshes the runtime-configuration projection for a single source.
    /// </summary>
    /// <param name="sourceId">ProCursor source identifier.</param>
    /// <param name="request">Refresh request payload.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <returns>The refreshed runtime-configuration projection.</returns>
    /// <response code="200">The projection was refreshed.</response>
    /// <response code="404">The source was not found.</response>
    /// <response code="409">The source cannot currently be refreshed.</response>
    [HttpPost("/internal/propr/procursor/runtime-config/sources/{sourceId:guid}/refresh")]
    public async Task<IActionResult> Refresh(
        Guid sourceId,
        [FromBody] ProCursorRuntimeConfigurationRefreshRequest request,
        CancellationToken ct)
    {
        try
        {
            return this.Ok(await projectionService.RefreshAsync(sourceId, request, ct));
        }
        catch (KeyNotFoundException)
        {
            return this.NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
    }
}
