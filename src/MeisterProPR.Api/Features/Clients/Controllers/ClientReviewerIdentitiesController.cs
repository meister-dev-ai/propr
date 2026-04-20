// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using FluentValidation.Results;
using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Features.Clients.Controllers;

/// <summary>Manages configured provider reviewer identities for one client provider connection.</summary>
[ApiController]
public sealed partial class ClientReviewerIdentitiesController(
    IClientScmConnectionRepository connectionRepository,
    IClientReviewerIdentityRepository reviewerIdentityRepository,
    IScmProviderRegistry providerRegistry,
    ILogger<ClientReviewerIdentitiesController> logger) : ControllerBase
{
    private const string ReviewerIdentityResolutionUnavailableMessage =
        "Reviewer identity resolution is unavailable for this provider connection.";

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

    private IActionResult ReviewerIdentityResolutionConflict(
        Guid clientId,
        Guid connectionId,
        string search,
        Exception ex)
    {
        LogReviewerIdentityResolutionConflict(logger, clientId, connectionId, search, ex);
        return this.Conflict(new { error = ReviewerIdentityResolutionUnavailableMessage });
    }

    /// <summary>Resolves candidate reviewer identities for one client provider connection.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="connectionId">Provider-connection identifier.</param>
    /// <param name="search">Search text used to find reviewer identities.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Reviewer identity candidates found.</response>
    /// <response code="400">Search text is missing.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">Client provider connection not found.</response>
    /// <response code="409">The provider onboarding identity capability is not registered.</response>
    [HttpGet("clients/{clientId:guid}/provider-connections/{connectionId:guid}/reviewer-identities/resolve")]
    [ProducesResponseType(typeof(IReadOnlyList<ResolvedReviewerIdentityResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ResolveReviewerIdentities(
        Guid clientId,
        Guid connectionId,
        [FromQuery] string? search,
        CancellationToken ct = default)
    {
        var auth = this.RequireClientAccess(clientId, ClientRole.ClientAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        if (string.IsNullOrWhiteSpace(search))
        {
            return this.BadRequest(new { error = "Search text is required." });
        }

        var connection = await connectionRepository.GetByIdAsync(clientId, connectionId, ct);
        if (connection is null)
        {
            return this.NotFound();
        }

        try
        {
            var reviewerIdentityService = providerRegistry.GetReviewerIdentityService(connection.ProviderFamily);
            var candidates = await reviewerIdentityService.ResolveCandidatesAsync(
                clientId,
                new ProviderHostRef(connection.ProviderFamily, connection.HostBaseUrl),
                search,
                ct);

            var response = candidates
                .Select(candidate => new ResolvedReviewerIdentityResponse(
                    connection.ClientId,
                    connection.Id,
                    connection.ProviderFamily,
                    candidate.ExternalUserId,
                    candidate.Login,
                    candidate.DisplayName,
                    candidate.IsBot))
                .ToList()
                .AsReadOnly();

            return this.Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return this.ReviewerIdentityResolutionConflict(clientId, connectionId, search, ex);
        }
    }

    /// <summary>Gets the configured reviewer identity for one client provider connection.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="connectionId">Provider-connection identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Configured reviewer identity found.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">Client provider connection or reviewer identity not found.</response>
    [HttpGet("clients/{clientId:guid}/provider-connections/{connectionId:guid}/reviewer-identity")]
    [ProducesResponseType(typeof(ClientReviewerIdentityDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetReviewerIdentity(
        Guid clientId,
        Guid connectionId,
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

        var reviewerIdentity = await reviewerIdentityRepository.GetByConnectionIdAsync(clientId, connectionId, ct);
        return reviewerIdentity is null ? this.NotFound() : this.Ok(reviewerIdentity);
    }

    /// <summary>Creates or replaces the configured reviewer identity for one client provider connection.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="connectionId">Provider-connection identifier.</param>
    /// <param name="request">Reviewer identity details.</param>
    /// <param name="validator">Validator for the request body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Reviewer identity stored.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">Client provider connection not found.</response>
    [HttpPut("clients/{clientId:guid}/provider-connections/{connectionId:guid}/reviewer-identity")]
    [ProducesResponseType(typeof(ClientReviewerIdentityDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PutReviewerIdentity(
        Guid clientId,
        Guid connectionId,
        [FromBody] SetClientReviewerIdentityRequest request,
        [FromServices] IValidator<SetClientReviewerIdentityRequest> validator,
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

        var reviewerIdentity = await reviewerIdentityRepository.UpsertAsync(
            clientId,
            connectionId,
            connection.ProviderFamily,
            request.ExternalUserId,
            request.Login,
            request.DisplayName,
            request.IsBot,
            ct);

        return reviewerIdentity is null ? this.NotFound() : this.Ok(reviewerIdentity);
    }

    /// <summary>Clears the configured reviewer identity for one client provider connection.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="connectionId">Provider-connection identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">Reviewer identity cleared.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">Client provider connection not found.</response>
    [HttpDelete("clients/{clientId:guid}/provider-connections/{connectionId:guid}/reviewer-identity")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteReviewerIdentity(
        Guid clientId,
        Guid connectionId,
        CancellationToken ct = default)
    {
        var auth = this.RequireClientAccess(clientId, ClientRole.ClientAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        var connection = await connectionRepository.GetByIdAsync(clientId, connectionId, ct);
        if (connection is null)
        {
            return this.NotFound();
        }

        await reviewerIdentityRepository.DeleteAsync(clientId, connectionId, ct);
        return this.NoContent();
    }
}

/// <summary>Request body for setting a client-scoped provider reviewer identity.</summary>
public sealed record SetClientReviewerIdentityRequest(
    string ExternalUserId,
    string Login,
    string DisplayName,
    bool IsBot);

/// <summary>Resolved reviewer identity candidate returned by provider onboarding APIs.</summary>
public sealed record ResolvedReviewerIdentityResponse(
    Guid ClientId,
    Guid ConnectionId,
    ScmProvider ProviderFamily,
    string ExternalUserId,
    string Login,
    string DisplayName,
    bool IsBot);
