// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.ProCursor.Service.Controllers;

/// <summary>
///     Internal query endpoints exposed by the extracted ProCursor host.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = MeisterProPR.Infrastructure.Features.ProCursor.Remote.ProCursorSharedKeyAuthenticationDefaults.Scheme)]
public sealed class InternalProCursorQueriesController(IProCursorGateway gateway) : ControllerBase
{
    [HttpPost("/internal/procursor/queries/knowledge")]
    public async Task<IActionResult> AskKnowledge(
        [FromBody] ProCursorKnowledgeQueryRequest request,
        CancellationToken ct)
    {
        try
        {
            return this.Ok(await gateway.AskKnowledgeAsync(request, ct));
        }
        catch (InvalidOperationException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
        catch (ProCursorDependencyUnavailableException ex)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
    }

    [HttpPost("/internal/procursor/queries/symbols")]
    public async Task<IActionResult> GetSymbolInsight(
        [FromBody] ProCursorSymbolQueryRequest request,
        CancellationToken ct)
    {
        try
        {
            return this.Ok(await gateway.GetSymbolInsightAsync(request, ct));
        }
        catch (InvalidOperationException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
        catch (ProCursorDependencyUnavailableException ex)
        {
            return this.StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ex.Message });
        }
    }
}
