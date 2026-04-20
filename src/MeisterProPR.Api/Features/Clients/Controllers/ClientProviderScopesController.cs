// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using FluentValidation.Results;
using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Features.Clients.Controllers;

/// <summary>Manages client-scoped SCM provider scope selections.</summary>
[ApiController]
public sealed partial class ClientProviderScopesController(
    IClientScmConnectionRepository connectionRepository,
    IClientScmScopeRepository scopeRepository,
    ILogger<ClientProviderScopesController> logger) : ControllerBase
{
    private const string DuplicateProviderScopeMessage =
        "A provider scope with the same external scope already exists for this connection.";

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

    private IActionResult? RequireClientAccess(Guid clientId, ClientRole minimumRole)
    {
        return AuthHelpers.RequireClientRole(this.HttpContext, clientId, minimumRole);
    }

    private IActionResult ProviderScopeConflict(Guid clientId, Guid connectionId, string externalScopeId, Exception ex)
    {
        LogProviderScopeConflict(logger, clientId, connectionId, externalScopeId, ex);
        return this.Conflict(new { error = DuplicateProviderScopeMessage });
    }

    /// <summary>Lists provider scopes configured for one client provider connection.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="connectionId">Provider-connection identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Provider scopes found.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">Client provider connection not found.</response>
    [HttpGet("clients/{clientId:guid}/provider-connections/{connectionId:guid}/scopes")]
    [ProducesResponseType(typeof(IReadOnlyList<ClientScmScopeDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProviderScopes(Guid clientId, Guid connectionId, CancellationToken ct = default)
    {
        var auth = this.RequireClientAccess(clientId, ClientRole.ClientUser);
        if (auth is not null)
        {
            return auth;
        }

        var connection = await connectionRepository.GetByIdAsync(clientId, connectionId, ct);
        if (connection is null)
        {
            return this.NotFound();
        }

        var scopes = await scopeRepository.GetByConnectionIdAsync(clientId, connectionId, ct);
        return this.Ok(scopes);
    }

    /// <summary>Gets one provider scope configured for one client provider connection.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="connectionId">Provider-connection identifier.</param>
    /// <param name="scopeId">Provider-scope identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Provider scope found.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">Client provider connection or scope not found.</response>
    [HttpGet("clients/{clientId:guid}/provider-connections/{connectionId:guid}/scopes/{scopeId:guid}")]
    [ProducesResponseType(typeof(ClientScmScopeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProviderScope(
        Guid clientId,
        Guid connectionId,
        Guid scopeId,
        CancellationToken ct = default)
    {
        var auth = this.RequireClientAccess(clientId, ClientRole.ClientUser);
        if (auth is not null)
        {
            return auth;
        }

        var connection = await connectionRepository.GetByIdAsync(clientId, connectionId, ct);
        if (connection is null)
        {
            return this.NotFound();
        }

        var scope = await scopeRepository.GetByIdAsync(clientId, connectionId, scopeId, ct);
        return scope is null ? this.NotFound() : this.Ok(scope);
    }

    /// <summary>Creates one provider scope selection for one client provider connection.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="connectionId">Provider-connection identifier.</param>
    /// <param name="request">Provider-scope details.</param>
    /// <param name="validator">Validator for the request body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">Provider scope created.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">Client provider connection not found.</response>
    /// <response code="409">A provider scope already exists for the same external scope in this connection.</response>
    [HttpPost("clients/{clientId:guid}/provider-connections/{connectionId:guid}/scopes")]
    [ProducesResponseType(typeof(ClientScmScopeDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateProviderScope(
        Guid clientId,
        Guid connectionId,
        [FromBody] CreateClientProviderScopeRequest request,
        [FromServices] IValidator<CreateClientProviderScopeRequest> validator,
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

        var connection = await connectionRepository.GetByIdAsync(clientId, connectionId, ct);
        if (connection is null)
        {
            return this.NotFound();
        }

        try
        {
            var created = await scopeRepository.AddAsync(
                clientId,
                connectionId,
                request.ScopeType,
                request.ExternalScopeId,
                request.ScopePath,
                request.DisplayName,
                request.IsEnabled,
                ct);

            if (created is null)
            {
                return this.NotFound();
            }

            return this.CreatedAtAction(
                nameof(this.GetProviderScope),
                new { clientId, connectionId, scopeId = created.Id },
                created);
        }
        catch (InvalidOperationException ex)
        {
            return this.ProviderScopeConflict(clientId, connectionId, request.ExternalScopeId, ex);
        }
    }

    /// <summary>Applies partial updates to one provider scope selection.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="connectionId">Provider-connection identifier.</param>
    /// <param name="scopeId">Provider-scope identifier.</param>
    /// <param name="request">Fields to update; omit a field to leave it unchanged.</param>
    /// <param name="validator">Validator for the request body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Provider scope updated.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">Client provider connection or scope not found.</response>
    [HttpPatch("clients/{clientId:guid}/provider-connections/{connectionId:guid}/scopes/{scopeId:guid}")]
    [ProducesResponseType(typeof(ClientScmScopeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PatchProviderScope(
        Guid clientId,
        Guid connectionId,
        Guid scopeId,
        [FromBody] PatchClientProviderScopeRequest request,
        [FromServices] IValidator<PatchClientProviderScopeRequest> validator,
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

        var existing = await scopeRepository.GetByIdAsync(clientId, connectionId, scopeId, ct);
        if (existing is null)
        {
            return this.NotFound();
        }

        var updated = await scopeRepository.UpdateAsync(
            clientId,
            connectionId,
            scopeId,
            request.DisplayName ?? existing.DisplayName,
            request.IsEnabled ?? existing.IsEnabled,
            ct);

        return updated is null ? this.NotFound() : this.Ok(updated);
    }

    /// <summary>Deletes one provider scope selection from one client provider connection.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="connectionId">Provider-connection identifier.</param>
    /// <param name="scopeId">Provider-scope identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">Provider scope deleted.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">Client provider connection or scope not found.</response>
    [HttpDelete("clients/{clientId:guid}/provider-connections/{connectionId:guid}/scopes/{scopeId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteProviderScope(
        Guid clientId,
        Guid connectionId,
        Guid scopeId,
        CancellationToken ct = default)
    {
        var auth = this.RequireClientAccess(clientId, ClientRole.ClientAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        var deleted = await scopeRepository.DeleteAsync(clientId, connectionId, scopeId, ct);
        return deleted ? this.NoContent() : this.NotFound();
    }
}

/// <summary>Request body for creating a client-scoped provider scope selection.</summary>
public sealed record CreateClientProviderScopeRequest(
    string ScopeType,
    string ExternalScopeId,
    string ScopePath,
    string DisplayName,
    bool IsEnabled = true);

/// <summary>Request body for patching a client-scoped provider scope selection.</summary>
public sealed record PatchClientProviderScopeRequest(
    string? DisplayName = null,
    bool? IsEnabled = null);
