// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.ProCursor.Infrastructure.Remote;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.ProCursor.Service.Controllers;

/// <summary>
///     Internal runtime-configuration cache maintenance endpoints for the extracted ProCursor host.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = MeisterProPR.Infrastructure.Features.ProCursor.Remote.ProCursorSharedKeyAuthenticationDefaults.Scheme)]
public sealed class InternalProCursorRuntimeConfigurationController(
    IProCursorRuntimeConfigurationCache cache) : ControllerBase
{
    [HttpPost("/internal/procursor/runtime-config/sources/{sourceId:guid}/invalidate")]
    public async Task<IActionResult> Invalidate(Guid sourceId, CancellationToken ct)
    {
        await cache.InvalidateAsync(sourceId, ct);
        return this.Ok();
    }
}
