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
///     Internal source-management endpoints exposed by the extracted ProCursor host.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = ProCursorSharedKeyAuthenticationDefaults.Scheme)]
[Route("internal/procursor/clients/{clientId:guid}/sources")]
public sealed class InternalProCursorSourcesController(IProCursorGateway gateway) : ControllerBase
{
    /// <summary>
    ///     Lists ProCursor knowledge sources for a client.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <returns>The client's knowledge sources.</returns>
    /// <response code="200">The knowledge sources were returned.</response>
    /// <response code="404">The client was not found.</response>
    /// <response code="409">The request conflicts with current ProCursor state.</response>
    /// <response code="503">A required ProPR dependency is unavailable.</response>
    [HttpGet]
    public async Task<IActionResult> ListSources(Guid clientId, CancellationToken ct)
    {
        try
        {
            return this.Ok(await gateway.ListSourcesAsync(clientId, ct));
        }
        catch (KeyNotFoundException)
        {
            return this.NotFound();
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
    ///     Creates a ProCursor knowledge source for a client.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="request">Knowledge-source registration payload.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <returns>The created knowledge source.</returns>
    /// <response code="200">The knowledge source was created.</response>
    /// <response code="404">The client was not found.</response>
    /// <response code="409">The request conflicts with current ProCursor state.</response>
    /// <response code="503">A required ProPR dependency is unavailable.</response>
    [HttpPost]
    public async Task<IActionResult> CreateSource(
        Guid clientId,
        [FromBody] ProCursorKnowledgeSourceRegistrationRequest request,
        CancellationToken ct)
    {
        try
        {
            return this.Ok(await gateway.CreateSourceAsync(clientId, request, ct));
        }
        catch (KeyNotFoundException)
        {
            return this.NotFound();
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
    ///     Queues an index refresh for a ProCursor knowledge source.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="sourceId">Knowledge-source identifier.</param>
    /// <param name="request">Refresh request payload.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <returns>The queued index job.</returns>
    /// <response code="200">The refresh job was queued.</response>
    /// <response code="404">The client or source was not found.</response>
    /// <response code="409">The request conflicts with current ProCursor state.</response>
    /// <response code="503">A required ProPR dependency is unavailable.</response>
    [HttpPost("{sourceId:guid}/refresh")]
    public async Task<IActionResult> QueueRefresh(
        Guid clientId,
        Guid sourceId,
        [FromBody] ProCursorRefreshRequest request,
        CancellationToken ct)
    {
        try
        {
            return this.Ok(await gateway.QueueRefreshAsync(clientId, sourceId, request, ct));
        }
        catch (KeyNotFoundException)
        {
            return this.NotFound();
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
    ///     Lists tracked branches for a ProCursor knowledge source.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="sourceId">Knowledge-source identifier.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <returns>The tracked branches for the source.</returns>
    /// <response code="200">Tracked branches were returned.</response>
    /// <response code="404">The client or source was not found.</response>
    /// <response code="409">The request conflicts with current ProCursor state.</response>
    /// <response code="503">A required ProPR dependency is unavailable.</response>
    [HttpGet("{sourceId:guid}/branches")]
    public async Task<IActionResult> ListTrackedBranches(Guid clientId, Guid sourceId, CancellationToken ct)
    {
        try
        {
            return this.Ok(await gateway.ListTrackedBranchesAsync(clientId, sourceId, ct));
        }
        catch (KeyNotFoundException)
        {
            return this.NotFound();
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
    ///     Adds a tracked branch to a ProCursor knowledge source.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="sourceId">Knowledge-source identifier.</param>
    /// <param name="request">Tracked-branch creation payload.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <returns>The created tracked branch.</returns>
    /// <response code="200">The tracked branch was created.</response>
    /// <response code="404">The client or source was not found.</response>
    /// <response code="409">The request conflicts with current ProCursor state.</response>
    /// <response code="503">A required ProPR dependency is unavailable.</response>
    [HttpPost("{sourceId:guid}/branches")]
    public async Task<IActionResult> AddTrackedBranch(
        Guid clientId,
        Guid sourceId,
        [FromBody] ProCursorTrackedBranchCreateRequest request,
        CancellationToken ct)
    {
        try
        {
            return this.Ok(await gateway.AddTrackedBranchAsync(clientId, sourceId, request, ct));
        }
        catch (KeyNotFoundException)
        {
            return this.NotFound();
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
    ///     Updates a tracked branch for a ProCursor knowledge source.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="sourceId">Knowledge-source identifier.</param>
    /// <param name="branchId">Tracked-branch identifier.</param>
    /// <param name="request">Tracked-branch update payload.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <returns>The updated tracked branch when found.</returns>
    /// <response code="200">The tracked branch was updated.</response>
    /// <response code="404">The client, source, or branch was not found.</response>
    /// <response code="409">The request conflicts with current ProCursor state.</response>
    /// <response code="503">A required ProPR dependency is unavailable.</response>
    [HttpPut("{sourceId:guid}/branches/{branchId:guid}")]
    public async Task<IActionResult> UpdateTrackedBranch(
        Guid clientId,
        Guid sourceId,
        Guid branchId,
        [FromBody] ProCursorTrackedBranchUpdateRequest request,
        CancellationToken ct)
    {
        try
        {
            var branch = await gateway.UpdateTrackedBranchAsync(clientId, sourceId, branchId, request, ct);
            return branch is null ? this.NotFound() : this.Ok(branch);
        }
        catch (KeyNotFoundException)
        {
            return this.NotFound();
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
    ///     Removes a tracked branch from a ProCursor knowledge source.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="sourceId">Knowledge-source identifier.</param>
    /// <param name="branchId">Tracked-branch identifier.</param>
    /// <param name="ct">Cancellation token for the request.</param>
    /// <returns>No content when the tracked branch is removed.</returns>
    /// <response code="204">The tracked branch was removed.</response>
    /// <response code="404">The client, source, or branch was not found.</response>
    /// <response code="409">The request conflicts with current ProCursor state.</response>
    /// <response code="503">A required ProPR dependency is unavailable.</response>
    [HttpDelete("{sourceId:guid}/branches/{branchId:guid}")]
    public async Task<IActionResult> RemoveTrackedBranch(
        Guid clientId,
        Guid sourceId,
        Guid branchId,
        CancellationToken ct)
    {
        try
        {
            return await gateway.RemoveTrackedBranchAsync(clientId, sourceId, branchId, ct)
                ? this.NoContent()
                : this.NotFound();
        }
        catch (KeyNotFoundException)
        {
            return this.NotFound();
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
