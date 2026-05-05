// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using FluentValidation.Results;
using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Manages clients and related admin configuration.</summary>
[ApiController]
public sealed partial class ClientsController(
    IClientAdminService clientAdminService,
    ITenantAdminService tenantAdminService) : ControllerBase
{
    private static ClientResponse ToClientResponse(ClientDto client)
    {
        return new ClientResponse(
            client.Id,
            client.DisplayName,
            client.IsActive,
            client.CreatedAt,
            client.CommentResolutionBehavior,
            client.CustomSystemMessage,
            client.ScmCommentPostingEnabled,
            client.TenantId,
            client.TenantSlug,
            client.TenantDisplayName);
    }

    private IActionResult? ValidateRequest(ValidationResult result)
    {
        if (result.IsValid)
        {
            return null;
        }

        foreach (var error in result.Errors)
        {
            this.ModelState.AddModelError(error.PropertyName, error.ErrorMessage);
        }

        return this.ValidationProblem();
    }

    /// <summary>
    ///     Returns null if the caller is a global admin.
    ///     Returns 403 Forbidden if the caller is authenticated but not admin.
    ///     Returns 401 Unauthorized if the caller is unauthenticated.
    /// </summary>
    private IActionResult? RequirePlatformAdmin()
    {
        return AuthHelpers.RequirePlatformAdmin(this.HttpContext);
    }

    private IActionResult? RequireClientAccess(Guid clientId, ClientRole minimumRole)
    {
        return AuthHelpers.RequireClientRole(this.HttpContext, clientId, minimumRole);
    }

    /// <summary>
    ///     Registers a new client. Requires a global admin JWT or <c>X-User-Pat</c>.
    /// </summary>
    /// <param name="request">Client registration details.</param>
    /// <param name="validator">Validator for the request body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">Client registered.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller is not a global admin.</response>
    [HttpPost("clients")]
    [ProducesResponseType(typeof(ClientResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CreateClient(
        [FromBody] CreateClientRequest request,
        [FromServices] IValidator<CreateClientRequest> validator,
        CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireAuthenticated(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        if (!AuthHelpers.IsAdmin(this.HttpContext))
        {
            var tenantAuth = request.TenantId == TenantCatalog.SystemTenantId
                ? this.StatusCode(StatusCodes.Status403Forbidden, new { error = "You do not have the required role for this tenant." })
                : AuthHelpers.RequireTenantRole(
                    this.HttpContext,
                    request.TenantId,
                    TenantRole.TenantAdministrator);
            if (tenantAuth is not null)
            {
                return tenantAuth;
            }
        }

        var validation = this.ValidateRequest(await validator.ValidateAsync(request, ct));
        if (validation is not null)
        {
            return validation;
        }

        if (await tenantAdminService.GetByIdAsync(request.TenantId, ct) is null)
        {
            return this.NotFound();
        }

        try
        {
            var client = await clientAdminService.CreateAsync(request.TenantId, request.DisplayName, ct);

            return this.CreatedAtAction(
                nameof(this.GetClient),
                new { clientId = client.Id },
                ToClientResponse(client));
        }
        catch (InvalidOperationException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
    }

    /// <summary>
    ///     Deletes a client and all its crawl configurations.
    ///     Requires a global admin JWT or <c>X-User-Pat</c>.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <response code="204">Client deleted.</response>
    /// <response code="401">Missing or invalid admin credentials.</response>
    /// <response code="404">Client not found.</response>
    [HttpDelete("clients/{clientId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteClient(Guid clientId)
    {
        var auth = this.RequirePlatformAdmin();
        if (auth is not null)
        {
            return auth;
        }

        var deleted = await clientAdminService.DeleteAsync(clientId);
        return deleted ? this.NoContent() : this.NotFound();
    }

    /// <summary>
    ///     Gets a single client by ID.
    ///     Requires valid user authentication and access to the requested client.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Client found.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="404">Client not found.</response>
    [HttpGet("clients/{clientId:guid}")]
    [ProducesResponseType(typeof(ClientResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetClient(Guid clientId, CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireAuthenticated(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        var isAdmin = AuthHelpers.IsAdmin(this.HttpContext);
        var client = await clientAdminService.GetByIdAsync(clientId, ct);
        if (client is null)
        {
            return this.NotFound();
        }

        if (!isAdmin && !AuthHelpers.GetClientRoles(this.HttpContext).ContainsKey(clientId))
        {
            return this.StatusCode(403, new { error = "Access denied." });
        }

        return this.Ok(ToClientResponse(client));
    }

    /// <summary>
    ///     Lists registered clients.
    ///     Admins receive all clients.
    ///     Non-admin users receive only clients for their assigned roles.
    ///     Requires a valid JWT bearer token or <c>X-User-Pat</c>.
    /// </summary>
    /// <response code="200">List of clients.</response>
    /// <response code="401">No valid credentials provided.</response>
    [HttpGet("clients")]
    [ProducesResponseType(typeof(IReadOnlyList<ClientResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetClients(CancellationToken ct = default)
    {
        var auth = AuthHelpers.RequireAuthenticated(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        var isAdmin = AuthHelpers.IsAdmin(this.HttpContext);
        if (isAdmin)
        {
            var all = await clientAdminService.GetAllAsync(ct);
            return this.Ok(all.Select(ToClientResponse).ToList());
        }

        var clientIds = AuthHelpers.GetClientRoles(this.HttpContext).Keys.ToList();
        if (clientIds.Count == 0)
        {
            return this.Ok(Array.Empty<ClientResponse>());
        }

        var scoped = await clientAdminService.GetByIdsAsync(clientIds, ct);
        return this.Ok(scoped.Select(ToClientResponse).ToList());
    }

    /// <summary>
    ///     Updates one or more fields of a client (display name, active status, review settings, custom system message).
    ///     Requires a global admin JWT or <c>X-User-Pat</c>.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="request">Fields to update; omit a field to leave it unchanged.</param>
    /// <param name="validator">Validator for the request body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Client updated.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">Missing or invalid admin credentials.</response>
    /// <response code="404">Client not found.</response>
    [HttpPatch("clients/{clientId:guid}")]
    [ProducesResponseType(typeof(ClientResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PatchClient(
        Guid clientId,
        [FromBody] PatchClientRequest request,
        [FromServices] IValidator<PatchClientRequest> validator,
        CancellationToken ct = default)
    {
        var auth = this.RequireClientAccess(clientId, ClientRole.ClientAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        var validation = this.ValidateRequest(await validator.ValidateAsync(request, ct));
        if (validation is not null)
        {
            return validation;
        }

        var client = await clientAdminService.PatchAsync(
            clientId,
            request.IsActive,
            request.DisplayName,
            request.CommentResolutionBehavior,
            request.CustomSystemMessage,
            request.ScmCommentPostingEnabled,
            ct);
        return client is null ? this.NotFound() : this.Ok(ToClientResponse(client));
    }
}

/// <summary>Client response — key, ADO secret, and credential metadata are never included.</summary>
public sealed record ClientResponse(
    Guid Id,
    string DisplayName,
    bool IsActive,
    DateTimeOffset CreatedAt,
    CommentResolutionBehavior CommentResolutionBehavior,
    string? CustomSystemMessage,
    bool ScmCommentPostingEnabled,
    Guid? TenantId,
    string? TenantSlug,
    string? TenantDisplayName);

/// <summary>Crawl configuration response.</summary>
public sealed record CrawlConfigResponse(
    Guid Id,
    Guid ClientId,
    ScmProvider Provider,
    Guid? OrganizationScopeId,
    string ProviderScopePath,
    string ProviderProjectKey,
    int CrawlIntervalSeconds,
    bool IsActive,
    DateTimeOffset CreatedAt,
    IReadOnlyList<CrawlRepoFilterResponse>? RepoFilters = null,
    ProCursorSourceScopeMode ProCursorSourceScopeMode = ProCursorSourceScopeMode.AllClientSources,
    IReadOnlyList<Guid>? ProCursorSourceIds = null,
    IReadOnlyList<Guid>? InvalidProCursorSourceIds = null,
    float? ReviewTemperature = null);

/// <summary>A single repo filter entry in a crawl config response.</summary>
public sealed record CrawlRepoFilterResponse(
    Guid Id,
    string RepositoryName,
    IReadOnlyList<string> TargetBranchPatterns,
    CanonicalSourceReferenceDto? CanonicalSourceRef = null,
    string? DisplayName = null);

/// <summary>Request body for creating a client.</summary>
public sealed record CreateClientRequest(string DisplayName, Guid TenantId);

/// <summary>
///     Request body for patching a client. All fields are optional; omitted fields are left unchanged.
///     Set <see cref="CustomSystemMessage" /> to <c>""</c> (empty string) to clear an existing value.
/// </summary>
public sealed record PatchClientRequest(
    bool? IsActive = null,
    string? DisplayName = null,
    CommentResolutionBehavior? CommentResolutionBehavior = null,
    string? CustomSystemMessage = null,
    bool? ScmCommentPostingEnabled = null);
