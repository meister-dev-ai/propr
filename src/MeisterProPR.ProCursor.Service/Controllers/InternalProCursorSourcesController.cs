// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.ProCursor.Service.Controllers;

/// <summary>
///     Internal source-management endpoints exposed by the extracted ProCursor host.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = MeisterProPR.Infrastructure.Features.ProCursor.Remote.ProCursorSharedKeyAuthenticationDefaults.Scheme)]
public sealed class InternalProCursorSourcesController(IProCursorGateway gateway) : ControllerBase
{
    [HttpGet("/internal/procursor/clients/{clientId:guid}/sources")]
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

    [HttpPost("/internal/procursor/clients/{clientId:guid}/sources")]
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

    [HttpPost("/internal/procursor/clients/{clientId:guid}/sources/{sourceId:guid}/refresh")]
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

    [HttpGet("/internal/procursor/clients/{clientId:guid}/sources/{sourceId:guid}/branches")]
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

    [HttpPost("/internal/procursor/clients/{clientId:guid}/sources/{sourceId:guid}/branches")]
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

    [HttpPut("/internal/procursor/clients/{clientId:guid}/sources/{sourceId:guid}/branches/{branchId:guid}")]
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

    [HttpDelete("/internal/procursor/clients/{clientId:guid}/sources/{sourceId:guid}/branches/{branchId:guid}")]
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
