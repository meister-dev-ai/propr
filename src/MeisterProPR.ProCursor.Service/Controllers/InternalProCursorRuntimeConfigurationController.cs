// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Infrastructure.Features.ProCursor.Remote;
using MeisterProPR.ProCursor.Infrastructure.Remote;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.ProCursor.Service.Controllers;

/// <summary>
///     Internal runtime-configuration cache maintenance endpoints for the extracted ProCursor host.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = ProCursorSharedKeyAuthenticationDefaults.Scheme)]
[Route("internal/procursor/runtime-config/sources/{sourceId:guid}")]
public sealed class InternalProCursorRuntimeConfigurationController(IProCursorRuntimeConfigurationCache cache) : ControllerBase
{
    /// <summary>
    ///     Invalidates the cached runtime configuration for a ProCursor source.
    /// </summary>
    /// <param name="sourceId">ProCursor source identifier.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <returns>An acknowledgement that invalidation completed.</returns>
    /// <response code="200">The runtime configuration cache entry was invalidated.</response>
    [HttpPost("invalidate")]
    public async Task<IActionResult> Invalidate(Guid sourceId, CancellationToken ct)
    {
        await cache.InvalidateAsync(sourceId, ct);
        return this.Ok();
    }
}
