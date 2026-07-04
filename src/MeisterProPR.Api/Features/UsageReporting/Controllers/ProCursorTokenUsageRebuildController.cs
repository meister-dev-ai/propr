// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>
///     Administrative endpoint that recomputes ProCursor token usage rollups.
/// </summary>
[ApiController]
[Route("admin/clients/{clientId:guid}/procursor/token-usage")]
public sealed class ProCursorTokenUsageRebuildController(
    IProCursorTokenUsageRebuildService rebuildService) : ControllerBase
{
    /// <summary>
    ///     Rebuilds ProCursor token usage rollups for the selected captured interval.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="request">Rebuild request payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Rollups rebuilt successfully.</response>
    /// <response code="400">The request payload was invalid.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller does not have administrator access to the client.</response>
    [HttpPost("rebuild")]
    [ProducesResponseType(typeof(ProCursorTokenUsageRebuildResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Rebuild(
        Guid clientId,
        [FromBody] ProCursorTokenUsageRebuildRequest request,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        if (request.To < request.From)
        {
            this.ModelState.AddModelError(nameof(request.To), "to must be greater than or equal to from.");
            return this.ValidationProblem();
        }

        return this.Ok(await rebuildService.RebuildAsync(clientId, request, ct));
    }
}
