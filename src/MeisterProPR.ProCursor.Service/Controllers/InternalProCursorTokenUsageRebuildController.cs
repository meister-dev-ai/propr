// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Features.ProCursor.Remote;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.ProCursor.Service.Controllers;

/// <summary>
///     Internal token-usage rollup rebuild endpoint for the extracted ProCursor host.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = ProCursorSharedKeyAuthenticationDefaults.Scheme)]
[Route("internal/procursor/clients/{clientId:guid}")]
public sealed class InternalProCursorTokenUsageRebuildController(IProCursorTokenUsageRebuildService rebuildService) : ControllerBase
{
    /// <summary>
    ///     Rebuilds token-usage rollups for a client and time range.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="request">Rebuild request payload.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <returns>The rebuild result.</returns>
    /// <response code="200">The token-usage rollups were rebuilt.</response>
    [HttpPost("token-usage/rebuild")]
    public async Task<ActionResult<ProCursorTokenUsageRebuildResponse>> Rebuild(
        Guid clientId,
        [FromBody] ProCursorTokenUsageRebuildRequest request,
        CancellationToken ct = default)
    {
        return this.Ok(await rebuildService.RebuildAsync(clientId, request, ct));
    }
}
