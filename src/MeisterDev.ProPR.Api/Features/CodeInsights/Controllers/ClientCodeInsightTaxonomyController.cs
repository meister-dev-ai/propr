// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Api.Extensions;
using MeisterDev.ProPR.Application.Features.CodeInsights.Taxonomy;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.Api.Features.CodeInsights.Controllers;

/// <summary>
///     Manages one client's finding-type taxonomy: the installation's fixed core set is read-only, the
///     client's custom tags are editable by a client administrator.
/// </summary>
[ApiController]
[Route("clients/{clientId:guid}/code-insights/taxonomy")]
public sealed class ClientCodeInsightTaxonomyController(ICodeInsightTaxonomyService taxonomyService)
    : ControllerBase
{
    /// <summary>Returns the client's full finding-type vocabulary, including retired custom tags.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The vocabulary.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    [HttpGet]
    [ProducesResponseType(typeof(CodeInsightTaxonomyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetTaxonomy(Guid clientId, CancellationToken ct)
    {
        // Reading the vocabulary needs client access only; changing it needs administration.
        var denied = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser);
        if (denied is not null)
        {
            return denied;
        }

        return this.Ok(await taxonomyService.GetTaxonomyAsync(clientId, ct));
    }

    /// <summary>Creates a custom finding type for the client.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="request">The tag to create.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">The tag was created.</response>
    /// <response code="400">The request was malformed.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="409">The slug shadows a core type or is already used by this client.</response>
    [HttpPost("custom-tags")]
    [ProducesResponseType(typeof(CodeInsightCustomTagDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateCustomTag(
        Guid clientId,
        [FromBody] CodeInsightCustomTagWriteRequest request,
        CancellationToken ct)
    {
        var denied = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (denied is not null)
        {
            return denied;
        }

        var result = await taxonomyService.CreateCustomTagAsync(clientId, request, ct);
        return result.Succeeded
            ? this.Created($"/clients/{clientId}/code-insights/taxonomy/custom-tags/{result.Tag!.Id}", result.Tag)
            : MapRejection(result);
    }

    /// <summary>Updates a custom finding type. Existing assignments are unaffected.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="tagId">Custom tag identifier.</param>
    /// <param name="request">The new slug, display name, and definition.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The tag was updated.</response>
    /// <response code="400">The request was malformed.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">The tag does not exist for this client.</response>
    /// <response code="409">The slug shadows a core type or is already used by this client.</response>
    [HttpPut("custom-tags/{tagId:guid}")]
    [ProducesResponseType(typeof(CodeInsightCustomTagDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateCustomTag(
        Guid clientId,
        Guid tagId,
        [FromBody] CodeInsightCustomTagWriteRequest request,
        CancellationToken ct)
    {
        var denied = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (denied is not null)
        {
            return denied;
        }

        var result = await taxonomyService.UpdateCustomTagAsync(clientId, tagId, request, ct);
        return result.Succeeded ? this.Ok(result.Tag) : MapRejection(result);
    }

    /// <summary>
    ///     Retires a custom finding type. It stops being offered for new findings; findings that already
    ///     carry it keep resolving.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="tagId">Custom tag identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The tag is retired.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">The tag does not exist for this client.</response>
    [HttpPost("custom-tags/{tagId:guid}/retire")]
    [ProducesResponseType(typeof(CodeInsightCustomTagDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RetireCustomTag(Guid clientId, Guid tagId, CancellationToken ct)
    {
        var denied = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
        if (denied is not null)
        {
            return denied;
        }

        var result = await taxonomyService.RetireCustomTagAsync(clientId, tagId, ct);
        return result.Succeeded ? this.Ok(result.Tag) : MapRejection(result);
    }

    private static IActionResult MapRejection(CodeInsightCustomTagWriteResult result)
    {
        var payload = new { error = result.Message };

        return result.Error switch
        {
            CodeInsightCustomTagWriteError.NotFound => new NotFoundObjectResult(payload),
            CodeInsightCustomTagWriteError.ShadowsCoreTag => new ConflictObjectResult(payload),
            CodeInsightCustomTagWriteError.SlugAlreadyUsed => new ConflictObjectResult(payload),
            _ => new BadRequestObjectResult(payload),
        };
    }
}
