// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using FluentValidation.Results;
using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Manages clients (admin), ADO credentials, and reviewer identity.</summary>
[ApiController]
public sealed partial class ClientsController(
    IClientAdminService clientAdminService,
    IClientAdoCredentialRepository adoCredentialRepository,
    IClientAdoOrganizationScopeRepository organizationScopeRepository,
    IUserRepository userRepository,
    ILogger<ClientsController> logger) : ControllerBase
{
    private static ClientResponse ToClientResponse(ClientDto client)
    {
        return new ClientResponse(
            client.Id,
            client.DisplayName,
            client.IsActive,
            client.CreatedAt,
            client.HasAdoCredentials,
            client.ReviewerId,
            client.CommentResolutionBehavior,
            client.CustomSystemMessage);
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
    private IActionResult? RequireAdmin()
    {
        return AuthHelpers.RequireAdmin(this.HttpContext);
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
        var auth = this.RequireAdmin();
        if (auth is not null)
        {
            return auth;
        }

        var validation = this.ValidateRequest(await validator.ValidateAsync(request, ct));
        if (validation is not null)
        {
            return validation;
        }

        var client = await clientAdminService.CreateAsync(request.DisplayName, ct);

        return this.CreatedAtAction(
            nameof(this.GetClient),
            new { clientId = client.Id },
            ToClientResponse(client));
    }

    /// <summary>
    ///     Removes ADO service principal credentials from a client.
    ///     Requires a global admin JWT or <c>X-User-Pat</c>.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">Credentials removed (or client had no credentials — idempotent).</response>
    /// <response code="401">Missing or invalid admin credentials.</response>
    /// <response code="404">Client not found.</response>
    [HttpDelete("clients/{clientId:guid}/ado-credentials")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAdoCredentials(Guid clientId, CancellationToken ct = default)
    {
        var auth = this.RequireAdmin();
        if (auth is not null)
        {
            return auth;
        }

        if (!await clientAdminService.ExistsAsync(clientId, ct))
        {
            return this.NotFound();
        }

        await adoCredentialRepository.ClearAsync(clientId, ct);
        return this.NoContent();
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
        var auth = this.RequireAdmin();
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
        var userId = AuthHelpers.GetUserId(this.HttpContext);

        var client = await clientAdminService.GetByIdAsync(clientId, ct);
        if (client is null)
        {
            return this.NotFound();
        }

        // Non-admin users can only see clients they're assigned to
        if (!isAdmin && userId is not null)
        {
            var user = await userRepository.GetByIdWithAssignmentsAsync(userId.Value, ct);
            var hasAccess = user?.ClientAssignments.Any(a => a.ClientId == clientId) ?? false;
            if (!hasAccess)
            {
                return this.StatusCode(403, new { error = "Access denied." });
            }
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
        var userId = AuthHelpers.GetUserId(this.HttpContext);

        if (isAdmin)
        {
            var all = await clientAdminService.GetAllAsync(ct);
            return this.Ok(all.Select(ToClientResponse).ToList());
        }

        // Non-admin: return scoped clients
        var user = await userRepository.GetByIdWithAssignmentsAsync(userId!.Value, ct);
        var clientIds = user?.ClientAssignments.Select(a => a.ClientId).ToList() ?? [];
        if (clientIds.Count == 0)
        {
            return this.Ok(Array.Empty<ClientResponse>());
        }

        var scoped = await clientAdminService.GetByIdsAsync(clientIds, ct);
        return this.Ok(scoped.Select(ToClientResponse).ToList());
    }

    /// <summary>
    ///     Updates one or more fields of a client (display name, active status, custom system message).
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
        var auth = this.RequireAdmin();
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
            ct);
        return client is null ? this.NotFound() : this.Ok(ToClientResponse(client));
    }

    /// <summary>
    ///     Sets or replaces ADO service principal credentials for a client.
    ///     Requires a global admin JWT or <c>X-User-Pat</c>.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="request">ADO credential details — all three fields required.</param>
    /// <param name="validator">Validator for the request body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">Credentials stored.</response>
    /// <response code="400">One or more fields are missing or blank.</response>
    /// <response code="401">Missing or invalid admin credentials.</response>
    /// <response code="404">Client not found.</response>
    [HttpPut("clients/{clientId:guid}/ado-credentials")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PutAdoCredentials(
        Guid clientId,
        [FromBody] SetAdoCredentialsRequest request,
        [FromServices] IValidator<SetAdoCredentialsRequest> validator,
        CancellationToken ct = default)
    {
        var auth = this.RequireAdmin();
        if (auth is not null)
        {
            return auth;
        }

        var validation = this.ValidateRequest(await validator.ValidateAsync(request, ct));
        if (validation is not null)
        {
            return validation;
        }

        if (!await clientAdminService.ExistsAsync(clientId, ct))
        {
            return this.NotFound();
        }

        await adoCredentialRepository.UpsertAsync(
            clientId,
            new ClientAdoCredentials(request.TenantId, request.ClientId, request.Secret),
            ct);

        return this.NoContent();
    }

    /// <summary>
    ///     Sets or replaces the ADO reviewer identity GUID for a client.
    ///     Requires global admin or <c>ClientAdministrator</c> role for the specified client.
    ///     Until this is set, review jobs for the client will be rejected.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="request">The ADO identity GUID of the AI service account.</param>
    /// <param name="validator">Validator for the request body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">Reviewer identity stored.</response>
    /// <response code="400"><paramref name="request" /> contains an empty GUID.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required role for this client.</response>
    /// <response code="404">Client not found.</response>
    [HttpPut("clients/{clientId:guid}/reviewer-identity")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PutReviewerIdentity(
        Guid clientId,
        [FromBody] SetReviewerIdentityRequest request,
        [FromServices] IValidator<SetReviewerIdentityRequest> validator,
        CancellationToken ct = default)
    {
        string actorType;

        if (AuthHelpers.IsAdmin(this.HttpContext))
        {
            actorType = "Admin";
        }
        else
        {
            var roleCheck = AuthHelpers.RequireClientRole(this.HttpContext, clientId, ClientRole.ClientAdministrator);
            if (roleCheck is not null)
            {
                return roleCheck;
            }

            actorType = "User";
        }

        var validation = this.ValidateRequest(await validator.ValidateAsync(request, ct));
        if (validation is not null)
        {
            return validation;
        }

        var found = await clientAdminService.SetReviewerIdentityAsync(clientId, request.ReviewerId, ct);
        if (!found)
        {
            return this.NotFound();
        }

        LogReviewerIdentityUpdated(logger, clientId, actorType);
        return this.NoContent();
    }

    /// <summary>
    ///     Lists Azure DevOps organization scopes configured for a client.
    ///     Requires global admin or <c>ClientUser</c> access for the specified client.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Organization scopes found.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">Client not found.</response>
    [HttpGet("clients/{clientId:guid}/ado-organization-scopes")]
    [ProducesResponseType(typeof(IReadOnlyList<ClientAdoOrganizationScopeDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAdoOrganizationScopes(Guid clientId, CancellationToken ct = default)
    {
        var auth = this.RequireClientAccess(clientId, ClientRole.ClientUser);
        if (auth is not null)
        {
            return auth;
        }

        if (!await clientAdminService.ExistsAsync(clientId, ct))
        {
            return this.NotFound();
        }

        var scopes = await organizationScopeRepository.GetByClientIdAsync(clientId, ct);
        return this.Ok(scopes);
    }

    /// <summary>
    ///     Gets one Azure DevOps organization scope configured for a client.
    ///     Requires global admin or <c>ClientUser</c> access for the specified client.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="scopeId">Organization-scope identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Organization scope found.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">Client or organization scope not found.</response>
    [HttpGet("clients/{clientId:guid}/ado-organization-scopes/{scopeId:guid}")]
    [ProducesResponseType(typeof(ClientAdoOrganizationScopeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAdoOrganizationScope(Guid clientId, Guid scopeId, CancellationToken ct = default)
    {
        var auth = this.RequireClientAccess(clientId, ClientRole.ClientUser);
        if (auth is not null)
        {
            return auth;
        }

        if (!await clientAdminService.ExistsAsync(clientId, ct))
        {
            return this.NotFound();
        }

        var scope = await organizationScopeRepository.GetByIdAsync(clientId, scopeId, ct);
        return scope is null ? this.NotFound() : this.Ok(scope);
    }

    /// <summary>
    ///     Creates an Azure DevOps organization scope for a client.
    ///     Requires global admin or <c>ClientAdministrator</c> access for the specified client.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="request">Organization-scope details.</param>
    /// <param name="validator">Validator for the request body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">Organization scope created.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">Client not found.</response>
    /// <response code="409">An organization scope for the URL already exists.</response>
    [HttpPost("clients/{clientId:guid}/ado-organization-scopes")]
    [ProducesResponseType(typeof(ClientAdoOrganizationScopeDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateAdoOrganizationScope(
        Guid clientId,
        [FromBody] CreateClientAdoOrganizationScopeRequest request,
        [FromServices] IValidator<CreateClientAdoOrganizationScopeRequest> validator,
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

        if (!await clientAdminService.ExistsAsync(clientId, ct))
        {
            return this.NotFound();
        }

        try
        {
            var created = await organizationScopeRepository.AddAsync(clientId, request.OrganizationUrl, request.DisplayName, ct);
            if (created is null)
            {
                return this.NotFound();
            }

            LogAdoOrganizationScopeCreated(logger, clientId, created.Id, created.VerificationStatus, created.IsEnabled);

            return this.CreatedAtAction(
                nameof(this.GetAdoOrganizationScope),
                new { clientId, scopeId = created.Id },
                created);
        }
        catch (InvalidOperationException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
    }

    /// <summary>
    ///     Applies partial updates to an Azure DevOps organization scope.
    ///     Requires global admin or <c>ClientAdministrator</c> access for the specified client.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="scopeId">Organization-scope identifier.</param>
    /// <param name="request">Fields to update; omit a field to leave it unchanged.</param>
    /// <param name="validator">Validator for the request body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Organization scope updated.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">Client or organization scope not found.</response>
    /// <response code="409">An organization scope for the URL already exists.</response>
    [HttpPatch("clients/{clientId:guid}/ado-organization-scopes/{scopeId:guid}")]
    [ProducesResponseType(typeof(ClientAdoOrganizationScopeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> PatchAdoOrganizationScope(
        Guid clientId,
        Guid scopeId,
        [FromBody] PatchClientAdoOrganizationScopeRequest request,
        [FromServices] IValidator<PatchClientAdoOrganizationScopeRequest> validator,
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

        var existing = await organizationScopeRepository.GetByIdAsync(clientId, scopeId, ct);
        if (existing is null)
        {
            return this.NotFound();
        }

        var organizationUrl = request.OrganizationUrl ?? existing.OrganizationUrl;
        var displayName = request.DisplayName ?? existing.DisplayName;
        var isEnabled = request.IsEnabled ?? existing.IsEnabled;

        try
        {
            var updated = await organizationScopeRepository.UpdateAsync(
                clientId,
                scopeId,
                organizationUrl,
                displayName,
                isEnabled,
                ct);

            if (updated is null)
            {
                return this.NotFound();
            }

            LogAdoOrganizationScopeUpdated(logger, clientId, scopeId, updated.VerificationStatus, updated.IsEnabled);
            return this.Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
    }

    /// <summary>
    ///     Deletes an Azure DevOps organization scope from a client.
    ///     Requires global admin or <c>ClientAdministrator</c> access for the specified client.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="scopeId">Organization-scope identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">Organization scope deleted.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">Client or organization scope not found.</response>
    [HttpDelete("clients/{clientId:guid}/ado-organization-scopes/{scopeId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteAdoOrganizationScope(Guid clientId, Guid scopeId, CancellationToken ct = default)
    {
        var auth = this.RequireClientAccess(clientId, ClientRole.ClientAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        var deleted = await organizationScopeRepository.DeleteAsync(clientId, scopeId, ct);
        if (deleted)
        {
            LogAdoOrganizationScopeDeleted(logger, clientId, scopeId);
        }

        return deleted ? this.NoContent() : this.NotFound();
    }


}

/// <summary>Client response — key, ADO secret, and credential metadata are never included.</summary>
public sealed record ClientResponse(
    Guid Id,
    string DisplayName,
    bool IsActive,
    DateTimeOffset CreatedAt,
    bool HasAdoCredentials,
    Guid? ReviewerId,
    CommentResolutionBehavior CommentResolutionBehavior,
    string? CustomSystemMessage);

/// <summary>Crawl configuration response.</summary>
public sealed record CrawlConfigResponse(
    Guid Id,
    Guid ClientId,
    Guid? OrganizationScopeId,
    string OrganizationUrl,
    string ProjectId,
    int CrawlIntervalSeconds,
    bool IsActive,
    DateTimeOffset CreatedAt,
    IReadOnlyList<CrawlRepoFilterResponse>? RepoFilters = null,
    ProCursorSourceScopeMode ProCursorSourceScopeMode = ProCursorSourceScopeMode.AllClientSources,
    IReadOnlyList<Guid>? ProCursorSourceIds = null,
    IReadOnlyList<Guid>? InvalidProCursorSourceIds = null);

/// <summary>A single repo filter entry in a crawl config response.</summary>
public sealed record CrawlRepoFilterResponse(
    Guid Id,
    string RepositoryName,
    IReadOnlyList<string> TargetBranchPatterns,
    CanonicalSourceReferenceDto? CanonicalSourceRef = null,
    string? DisplayName = null);

/// <summary>Request body for creating a client.</summary>
public sealed record CreateClientRequest(string DisplayName);

/// <summary>
///     Request body for patching a client. All fields are optional; omitted fields are left unchanged.
///     Set <see cref="CustomSystemMessage" /> to <c>""</c> (empty string) to clear an existing value.
/// </summary>
public sealed record PatchClientRequest(
    bool? IsActive = null,
    string? DisplayName = null,
    CommentResolutionBehavior? CommentResolutionBehavior = null,
    string? CustomSystemMessage = null);

/// <summary>Request body for setting ADO service principal credentials.</summary>
public sealed record SetAdoCredentialsRequest(string TenantId, string ClientId, string Secret);

/// <summary>Request body for creating a client-scoped Azure DevOps organization scope.</summary>
public sealed record CreateClientAdoOrganizationScopeRequest(string OrganizationUrl, string? DisplayName);

/// <summary>
///     Request body for patching a client-scoped Azure DevOps organization scope.
///     Omit a field to leave it unchanged.
/// </summary>
public sealed record PatchClientAdoOrganizationScopeRequest(
    string? OrganizationUrl = null,
    string? DisplayName = null,
    bool? IsEnabled = null);

/// <summary>Request body for setting the ADO reviewer identity on a client.</summary>
public sealed record SetReviewerIdentityRequest(Guid ReviewerId);

