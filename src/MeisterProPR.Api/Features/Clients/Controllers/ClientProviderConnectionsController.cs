// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using FluentValidation.Results;
using MeisterProPR.Api.Extensions;
using MeisterProPR.Api.Validators;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Clients.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Features.Clients.Controllers;

/// <summary>Manages client-scoped SCM provider connections.</summary>
[ApiController]
public sealed partial class ClientProviderConnectionsController(
    IClientAdminService clientAdminService,
    IClientScmConnectionRepository connectionRepository,
    IClientScmScopeRepository scopeRepository,
    IScmProviderRegistry providerRegistry,
    IProviderReadinessEvaluator readinessEvaluator,
    IProviderOperationalStatusService providerOperationalStatusService,
    ILogger<ClientProviderConnectionsController> logger,
    IProviderActivationService? providerActivationService = null) : ControllerBase
{
    private const string DuplicateProviderConnectionMessage =
        "A provider connection with the same provider family and host already exists for this client.";

    private const string DisabledProviderMessage =
        "The selected provider family is currently disabled by system administration.";

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

    private IActionResult? ValidateSupportedAuthenticationConfiguration(
        ScmProvider providerFamily,
        ScmAuthenticationKind authenticationKind,
        string? oAuthTenantId,
        string? oAuthClientId)
    {
        if (!CreateClientProviderConnectionRequestValidator.IsSupportedAuthenticationKind(
                providerFamily,
                authenticationKind))
        {
            this.ModelState.AddModelError(
                nameof(CreateClientProviderConnectionRequest.AuthenticationKind),
                CreateClientProviderConnectionRequestValidator.GetUnsupportedAuthenticationKindMessage(providerFamily));
        }

        if (CreateClientProviderConnectionRequestValidator.RequiresOAuthMetadata(providerFamily, authenticationKind))
        {
            if (string.IsNullOrWhiteSpace(oAuthTenantId))
            {
                this.ModelState.AddModelError(
                    nameof(CreateClientProviderConnectionRequest.OAuthTenantId),
                    "OAuthTenantId is required for Azure DevOps OAuth client-credentials connections.");
            }

            if (string.IsNullOrWhiteSpace(oAuthClientId))
            {
                this.ModelState.AddModelError(
                    nameof(CreateClientProviderConnectionRequest.OAuthClientId),
                    "OAuthClientId is required for Azure DevOps OAuth client-credentials connections.");
            }
        }

        return this.ModelState.ErrorCount == 0 ? null : this.ValidationProblem();
    }

    private static void EnsureSupportedAuthenticationConfiguration(
        ScmProvider providerFamily,
        ScmAuthenticationKind authenticationKind,
        string? oAuthTenantId,
        string? oAuthClientId)
    {
        if (!CreateClientProviderConnectionRequestValidator.IsSupportedAuthenticationKind(
                providerFamily,
                authenticationKind))
        {
            throw new InvalidOperationException(CreateClientProviderConnectionRequestValidator.GetUnsupportedAuthenticationKindMessage(providerFamily));
        }

        if (CreateClientProviderConnectionRequestValidator.RequiresOAuthMetadata(providerFamily, authenticationKind))
        {
            if (string.IsNullOrWhiteSpace(oAuthTenantId))
            {
                throw new InvalidOperationException("OAuthTenantId is required for Azure DevOps OAuth client-credentials connections.");
            }

            if (string.IsNullOrWhiteSpace(oAuthClientId))
            {
                throw new InvalidOperationException("OAuthClientId is required for Azure DevOps OAuth client-credentials connections.");
            }
        }
    }

    private IActionResult ProviderConnectionConflict(
        string operation,
        Guid clientId,
        ScmProvider providerFamily,
        string hostBaseUrl,
        Exception ex)
    {
        LogProviderConnectionConflict(logger, operation, clientId, providerFamily, hostBaseUrl, ex);
        return this.Conflict(new { error = DuplicateProviderConnectionMessage });
    }

    private async Task<bool> IsProviderEnabledAsync(ScmProvider providerFamily, CancellationToken ct)
    {
        return providerActivationService is null || await providerActivationService.IsEnabledAsync(providerFamily, ct);
    }

    private async Task<ClientScmConnectionDto> EnrichConnectionAsync(
        Guid clientId,
        ClientScmConnectionDto connection,
        CancellationToken ct)
    {
        var readiness = await readinessEvaluator.EvaluateAsync(clientId, connection, ct);
        return ApplyReadiness(connection, readiness);
    }

    private async Task<IReadOnlyList<ClientScmConnectionDto>> EnrichConnectionsAsync(
        Guid clientId,
        IReadOnlyList<ClientScmConnectionDto> connections,
        CancellationToken ct)
    {
        var enriched = new List<ClientScmConnectionDto>(connections.Count);
        foreach (var connection in connections)
        {
            enriched.Add(await this.EnrichConnectionAsync(clientId, connection, ct));
        }

        return enriched.AsReadOnly();
    }

    private static ClientScmConnectionDto ApplyReadiness(
        ClientScmConnectionDto connection,
        ProviderConnectionReadinessResult readiness)
    {
        return connection with
        {
            ReadinessLevel = readiness.ReadinessLevel,
            ReadinessReason = readiness.ReadinessReason,
            HostVariant = readiness.HostVariant,
            MissingReadinessCriteria = readiness.MissingCriteria,
        };
    }

    /// <summary>Lists provider connections configured for a client.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Provider connections found.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">Client not found.</response>
    [HttpGet("clients/{clientId:guid}/provider-connections")]
    [ProducesResponseType(typeof(IReadOnlyList<ClientScmConnectionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProviderConnections(Guid clientId, CancellationToken ct = default)
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

        var connections = await connectionRepository.GetByClientIdAsync(clientId, ct);
        return this.Ok(await this.EnrichConnectionsAsync(clientId, connections, ct));
    }

    /// <summary>Gets one provider connection configured for a client.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="connectionId">Provider-connection identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Provider connection found.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">Client or provider connection not found.</response>
    [HttpGet("clients/{clientId:guid}/provider-connections/{connectionId:guid}")]
    [ProducesResponseType(typeof(ClientScmConnectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProviderConnection(
        Guid clientId,
        Guid connectionId,
        CancellationToken ct = default)
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

        var connection = await connectionRepository.GetByIdAsync(clientId, connectionId, ct);
        return connection is null
            ? this.NotFound()
            : this.Ok(await this.EnrichConnectionAsync(clientId, connection, ct));
    }

    /// <summary>Lists authoritative provider operational status for a client.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Provider operational status found.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">Client not found.</response>
    [HttpGet("clients/{clientId:guid}/provider-operations/status")]
    [ProducesResponseType(typeof(ProviderOperationalStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProviderOperationalStatus(Guid clientId, CancellationToken ct = default)
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

        return this.Ok(await providerOperationalStatusService.GetForClientAsync(clientId, ct));
    }

    /// <summary>Lists recent provider-connection operational audit entries for a client.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="take">Maximum number of entries to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Provider audit entries found.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">Client not found.</response>
    [HttpGet("clients/{clientId:guid}/provider-operations/audit-trail")]
    [ProducesResponseType(typeof(IReadOnlyList<ProviderConnectionAuditEntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProviderConnectionAuditTrail(
        Guid clientId,
        [FromQuery] int take = 20,
        CancellationToken ct = default)
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

        var entries = await clientAdminService.GetProviderConnectionAuditTrailAsync(clientId, take, ct);
        return this.Ok(entries);
    }

    /// <summary>Creates a provider connection for a client.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="request">Provider-connection details.</param>
    /// <param name="validator">Validator for the request body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">Provider connection created.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">Client not found.</response>
    /// <response code="409">A provider connection already exists for the same provider family and host.</response>
    [HttpPost("clients/{clientId:guid}/provider-connections")]
    [ProducesResponseType(typeof(ClientScmConnectionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateProviderConnection(
        Guid clientId,
        [FromBody] CreateClientProviderConnectionRequest request,
        [FromServices] IValidator<CreateClientProviderConnectionRequest> validator,
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

        if (!await this.IsProviderEnabledAsync(request.ProviderFamily, ct))
        {
            return this.Conflict(new { error = DisabledProviderMessage });
        }

        var supportedAuthenticationValidation = this.ValidateSupportedAuthenticationConfiguration(
            request.ProviderFamily,
            request.AuthenticationKind,
            request.OAuthTenantId,
            request.OAuthClientId);
        if (supportedAuthenticationValidation is not null)
        {
            return supportedAuthenticationValidation;
        }

        try
        {
            var created = await connectionRepository.AddAsync(
                clientId,
                request.ProviderFamily,
                request.HostBaseUrl,
                request.AuthenticationKind,
                request.OAuthTenantId,
                request.OAuthClientId,
                request.DisplayName,
                request.Secret,
                request.IsActive,
                ct);

            if (created is null)
            {
                return this.NotFound();
            }

            var enriched = await this.EnrichConnectionAsync(clientId, created, ct);
            return this.CreatedAtAction(
                nameof(this.GetProviderConnection),
                new { clientId, connectionId = enriched.Id },
                enriched);
        }
        catch (InvalidOperationException ex)
        {
            return this.ProviderConnectionConflict(
                "create",
                clientId,
                request.ProviderFamily,
                request.HostBaseUrl,
                ex);
        }
    }

    /// <summary>Applies partial updates to one provider connection.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="connectionId">Provider-connection identifier.</param>
    /// <param name="request">Fields to update; omit a field to leave it unchanged.</param>
    /// <param name="validator">Validator for the request body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Provider connection updated.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">Client or provider connection not found.</response>
    /// <response code="409">A provider connection already exists for the same provider family and host.</response>
    [HttpPatch("clients/{clientId:guid}/provider-connections/{connectionId:guid}")]
    [ProducesResponseType(typeof(ClientScmConnectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> PatchProviderConnection(
        Guid clientId,
        Guid connectionId,
        [FromBody] PatchClientProviderConnectionRequest request,
        [FromServices] IValidator<PatchClientProviderConnectionRequest> validator,
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

        var existing = await connectionRepository.GetByIdAsync(clientId, connectionId, ct);
        if (existing is null)
        {
            return this.NotFound();
        }

        var effectiveAuthenticationKind = request.AuthenticationKind ?? existing.AuthenticationKind;
        var effectiveOAuthTenantId = request.OAuthTenantId ?? existing.OAuthTenantId;
        var effectiveOAuthClientId = request.OAuthClientId ?? existing.OAuthClientId;

        var supportedAuthenticationValidation = this.ValidateSupportedAuthenticationConfiguration(
            existing.ProviderFamily,
            effectiveAuthenticationKind,
            effectiveOAuthTenantId,
            effectiveOAuthClientId);
        if (supportedAuthenticationValidation is not null)
        {
            return supportedAuthenticationValidation;
        }

        try
        {
            var updated = await connectionRepository.UpdateAsync(
                clientId,
                connectionId,
                request.HostBaseUrl ?? existing.HostBaseUrl,
                effectiveAuthenticationKind,
                effectiveOAuthTenantId,
                effectiveOAuthClientId,
                request.DisplayName ?? existing.DisplayName,
                request.Secret,
                request.IsActive ?? existing.IsActive,
                ct);

            return updated is null ? this.NotFound() : this.Ok(await this.EnrichConnectionAsync(clientId, updated, ct));
        }
        catch (InvalidOperationException ex)
        {
            return this.ProviderConnectionConflict(
                "patch",
                clientId,
                existing.ProviderFamily,
                request.HostBaseUrl ?? existing.HostBaseUrl,
                ex);
        }
    }

    /// <summary>Deletes one provider connection from a client.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="connectionId">Provider-connection identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">Provider connection deleted.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">Client or provider connection not found.</response>
    [HttpDelete("clients/{clientId:guid}/provider-connections/{connectionId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteProviderConnection(
        Guid clientId,
        Guid connectionId,
        CancellationToken ct = default)
    {
        var auth = this.RequireClientAccess(clientId, ClientRole.ClientAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        var deleted = await connectionRepository.DeleteAsync(clientId, connectionId, ct);
        return deleted ? this.NoContent() : this.NotFound();
    }

    /// <summary>Verifies one provider connection has the onboarding capabilities required for this client.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="connectionId">Provider-connection identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Provider connection verification state updated.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">Client or provider connection not found.</response>
    [HttpPost("clients/{clientId:guid}/provider-connections/{connectionId:guid}/verify")]
    [ProducesResponseType(typeof(ClientScmConnectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> VerifyProviderConnection(
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

        var verificationStatus = "verified";
        string? verificationError = null;

        try
        {
            EnsureSupportedAuthenticationConfiguration(
                connection.ProviderFamily,
                connection.AuthenticationKind,
                connection.OAuthTenantId,
                connection.OAuthClientId);

            if (connection.ProviderFamily == ScmProvider.AzureDevOps)
            {
                var enabledScope = (await scopeRepository.GetByConnectionIdAsync(clientId, connectionId, ct))
                    .FirstOrDefault(scope => scope.IsEnabled);

                if (enabledScope is null)
                {
                    throw new InvalidOperationException("Add an enabled organization scope before verifying Azure DevOps provider connections.");
                }

                var discoveryService = providerRegistry.GetProviderAdminDiscoveryService(connection.ProviderFamily);
                await discoveryService.ListProjectsAsync(clientId, enabledScope.Id, ct);
            }
            else
            {
                var host = new ProviderHostRef(connection.ProviderFamily, connection.HostBaseUrl);
                var repositoryDiscoveryProvider =
                    providerRegistry.GetRepositoryDiscoveryProvider(connection.ProviderFamily);
                await repositoryDiscoveryProvider.ListScopesAsync(clientId, host, ct);
            }

            providerRegistry.GetReviewerIdentityService(connection.ProviderFamily);
        }
        catch (InvalidOperationException ex)
        {
            verificationStatus = "failed";
            verificationError = ex.Message;
        }
        catch (Exception ex)
        {
            verificationStatus = "failed";
            verificationError = ex.Message;
        }

        var updated = await connectionRepository.UpdateVerificationAsync(
            clientId,
            connectionId,
            verificationStatus,
            DateTimeOffset.UtcNow,
            verificationError,
            ct);

        return updated is null ? this.NotFound() : this.Ok(await this.EnrichConnectionAsync(clientId, updated, ct));
    }
}

/// <summary>Request body for creating a client-scoped provider connection.</summary>
public sealed record CreateClientProviderConnectionRequest(
    ScmProvider ProviderFamily,
    string HostBaseUrl,
    ScmAuthenticationKind AuthenticationKind,
    string? OAuthTenantId,
    string? OAuthClientId,
    string DisplayName,
    string Secret,
    bool IsActive = true);

/// <summary>Request body for patching a client-scoped provider connection.</summary>
public sealed record PatchClientProviderConnectionRequest(
    string? HostBaseUrl = null,
    ScmAuthenticationKind? AuthenticationKind = null,
    string? OAuthTenantId = null,
    string? OAuthClientId = null,
    string? DisplayName = null,
    string? Secret = null,
    bool? IsActive = null);
