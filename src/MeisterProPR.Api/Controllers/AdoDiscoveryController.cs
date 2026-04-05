// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Provides client-scoped Azure DevOps discovery data for guided admin configuration flows.</summary>
[ApiController]
public sealed class AdoDiscoveryController(IAdoDiscoveryService adoDiscoveryService) : ControllerBase
{
    /// <summary>
    ///     Lists projects available within one configured Azure DevOps organization scope.
    ///     Requires global admin or <c>ClientUser</c> access for the specified client.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="organizationScopeId">Organization-scope identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Projects found.</response>
    /// <response code="400">The query is invalid.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">The organization scope was not found.</response>
    [HttpGet("/admin/clients/{clientId:guid}/ado/discovery/projects")]
    [ProducesResponseType(typeof(IReadOnlyList<AdoProjectOptionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProjects(Guid clientId, [FromQuery] Guid organizationScopeId, CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser);
        if (auth is not null)
        {
            return auth;
        }

        if (organizationScopeId == Guid.Empty)
        {
            this.ModelState.AddModelError(nameof(organizationScopeId), "organizationScopeId is required.");
            return this.ValidationProblem();
        }

        try
        {
            var projects = await adoDiscoveryService.ListProjectsAsync(clientId, organizationScopeId, ct);
            return this.Ok(projects);
        }
        catch (KeyNotFoundException ex)
        {
            return this.NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return this.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    ///     Lists repositories or wikis available within one configured Azure DevOps project.
    ///     Requires global admin or <c>ClientUser</c> access for the specified client.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="organizationScopeId">Organization-scope identifier.</param>
    /// <param name="projectId">Azure DevOps project identifier.</param>
    /// <param name="sourceKind">Source type: <c>repository</c> or <c>adoWiki</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Sources found.</response>
    /// <response code="400">The query is invalid.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">The organization scope was not found.</response>
    [HttpGet("/admin/clients/{clientId:guid}/ado/discovery/sources")]
    [ProducesResponseType(typeof(IReadOnlyList<AdoSourceOptionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSources(
        Guid clientId,
        [FromQuery] Guid organizationScopeId,
        [FromQuery] string projectId,
        [FromQuery] string sourceKind,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser);
        if (auth is not null)
        {
            return auth;
        }

        if (this.ValidateDiscoveryQuery(organizationScopeId, projectId) is IActionResult validation)
        {
            return validation;
        }

        if (!TryParseSourceKind(sourceKind, out var parsedSourceKind))
        {
            this.ModelState.AddModelError(nameof(sourceKind), "sourceKind must be 'repository' or 'adoWiki'.");
            return this.ValidationProblem();
        }

        try
        {
            var sources = await adoDiscoveryService.ListSourcesAsync(clientId, organizationScopeId, projectId, parsedSourceKind, ct);
            return this.Ok(sources);
        }
        catch (KeyNotFoundException ex)
        {
            return this.NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return this.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return this.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    ///     Lists branches available for one discovered repository or wiki source.
    ///     Requires global admin or <c>ClientUser</c> access for the specified client.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="organizationScopeId">Organization-scope identifier.</param>
    /// <param name="projectId">Azure DevOps project identifier.</param>
    /// <param name="sourceKind">Source type: <c>repository</c> or <c>adoWiki</c>.</param>
    /// <param name="canonicalSourceProvider">Canonical source-reference provider.</param>
    /// <param name="canonicalSourceValue">Canonical source-reference value.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Branches found.</response>
    /// <response code="400">The query is invalid.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">The organization scope was not found.</response>
    [HttpGet("/admin/clients/{clientId:guid}/ado/discovery/branches")]
    [ProducesResponseType(typeof(IReadOnlyList<AdoBranchOptionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBranches(
        Guid clientId,
        [FromQuery] Guid organizationScopeId,
        [FromQuery] string projectId,
        [FromQuery] string sourceKind,
        [FromQuery] string canonicalSourceProvider,
        [FromQuery] string canonicalSourceValue,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser);
        if (auth is not null)
        {
            return auth;
        }

        if (this.ValidateDiscoveryQuery(organizationScopeId, projectId) is IActionResult validation)
        {
            return validation;
        }

        if (!TryParseSourceKind(sourceKind, out var parsedSourceKind))
        {
            this.ModelState.AddModelError(nameof(sourceKind), "sourceKind must be 'repository' or 'adoWiki'.");
            return this.ValidationProblem();
        }

        if (string.IsNullOrWhiteSpace(canonicalSourceProvider))
        {
            this.ModelState.AddModelError(nameof(canonicalSourceProvider), "canonicalSourceProvider is required.");
        }

        if (string.IsNullOrWhiteSpace(canonicalSourceValue))
        {
            this.ModelState.AddModelError(nameof(canonicalSourceValue), "canonicalSourceValue is required.");
        }

        if (!this.ModelState.IsValid)
        {
            return this.ValidationProblem();
        }

        try
        {
            var branches = await adoDiscoveryService.ListBranchesAsync(
                clientId,
                organizationScopeId,
                projectId,
                parsedSourceKind,
                new CanonicalSourceReferenceDto(canonicalSourceProvider.Trim(), canonicalSourceValue.Trim()),
                ct);
            return this.Ok(branches);
        }
        catch (KeyNotFoundException ex)
        {
            return this.NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return this.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return this.BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    ///     Lists repository options and branch suggestions suitable for crawl-filter configuration.
    ///     Requires global admin or <c>ClientUser</c> access for the specified client.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="organizationScopeId">Organization-scope identifier.</param>
    /// <param name="projectId">Azure DevOps project identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Crawl-filter options found.</response>
    /// <response code="400">The query is invalid.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">The organization scope was not found.</response>
    [HttpGet("/admin/clients/{clientId:guid}/ado/discovery/crawl-filters")]
    [ProducesResponseType(typeof(IReadOnlyList<AdoCrawlFilterOptionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCrawlFilters(
        Guid clientId,
        [FromQuery] Guid organizationScopeId,
        [FromQuery] string projectId,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientUser);
        if (auth is not null)
        {
            return auth;
        }

        if (this.ValidateDiscoveryQuery(organizationScopeId, projectId) is IActionResult validation)
        {
            return validation;
        }

        try
        {
            var crawlFilters = await adoDiscoveryService.ListCrawlFiltersAsync(clientId, organizationScopeId, projectId, ct);
            return this.Ok(crawlFilters);
        }
        catch (KeyNotFoundException ex)
        {
            return this.NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return this.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return this.BadRequest(new { error = ex.Message });
        }
    }

    private IActionResult? ValidateDiscoveryQuery(Guid organizationScopeId, string projectId)
    {
        if (organizationScopeId == Guid.Empty)
        {
            this.ModelState.AddModelError(nameof(organizationScopeId), "organizationScopeId is required.");
        }

        if (string.IsNullOrWhiteSpace(projectId))
        {
            this.ModelState.AddModelError(nameof(projectId), "projectId is required.");
        }

        return this.ModelState.IsValid ? null : this.ValidationProblem();
    }

    private static bool TryParseSourceKind(string sourceKind, out ProCursorSourceKind parsedSourceKind)
    {
        if (Enum.TryParse(sourceKind, ignoreCase: true, out parsedSourceKind))
        {
            return parsedSourceKind is ProCursorSourceKind.Repository or ProCursorSourceKind.AdoWiki;
        }

        return false;
    }
}
