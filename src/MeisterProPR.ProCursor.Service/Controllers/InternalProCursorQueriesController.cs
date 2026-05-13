// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Features.ProCursor.Remote;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.ProCursor.Service.Controllers;

/// <summary>
///     Internal query endpoints exposed by the extracted ProCursor host.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = ProCursorSharedKeyAuthenticationDefaults.Scheme)]
public sealed class InternalProCursorQueriesController(IProCursorGateway gateway) : ControllerBase
{
    /// <summary>
    ///     Executes a knowledge query against the extracted ProCursor host.
    /// </summary>
    /// <param name="request">Knowledge-query request payload.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <returns>The knowledge-query answer.</returns>
    /// <response code="200">The knowledge query completed successfully.</response>
    /// <response code="409">The request conflicts with current ProCursor state.</response>
    /// <response code="503">A required ProPR dependency is unavailable.</response>
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

    /// <summary>
    ///     Resolves symbol insight from the extracted ProCursor host.
    /// </summary>
    /// <param name="request">Symbol-query request payload.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <returns>The resolved symbol insight.</returns>
    /// <response code="200">The symbol insight was returned.</response>
    /// <response code="409">The request conflicts with current ProCursor state.</response>
    /// <response code="503">A required ProPR dependency is unavailable.</response>
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
