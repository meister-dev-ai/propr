// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Features.ProCursor.Broker.Services;
using MeisterProPR.Application.DTOs.ProCursor;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Features.ProCursor.Broker.Controllers;

/// <summary>
///     Internal runtime-configuration endpoints used by the extracted ProCursor runtime.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = MeisterProPR.Infrastructure.Features.ProCursor.Remote.ProCursorSharedKeyAuthenticationDefaults.Scheme)]
public sealed class ProCursorRuntimeConfigurationController(
    ProCursorRuntimeConfigurationProjectionService projectionService) : ControllerBase
{
    [HttpGet("/internal/propr/procursor/runtime-config/enabled")]
    public async Task<IActionResult> ListEnabled(CancellationToken ct)
    {
        return this.Ok(await projectionService.ListEnabledAsync(ct));
    }

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
