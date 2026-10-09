// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.Ai.Providers.Diagnostics;
using MeisterDev.Ai.Providers.Egress;
using System.Text.Json.Serialization;
using FluentValidation;
using FluentValidation.Results;
using MeisterDev.ProPR.Api.Extensions;
using MeisterDev.ProPR.Api.Features.Licensing;
using MeisterDev.ProPR.Api.Validators;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Clients.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.AspNetCore.Mvc;
using MeisterDev.ProPR.Web;
using ConnectionResponse = MeisterDev.ProPR.Api.Features.Clients.Contracts.ClientScmConnectionDto;

namespace MeisterDev.ProPR.Api.Features.Clients.Controllers;

/// <summary>Manages client-scoped SCM provider connections.</summary>
[ApiController]
[Route("clients/{clientId:guid}")]
public sealed partial class ClientProviderConnectionsController(
    IClientAdminService clientAdminService,
    IClientScmConnectionRepository connectionRepository,
    IClientScmScopeRepository scopeRepository,
    IScmProviderRegistry providerRegistry,
    IProviderConnectionConfigurationService connectionConfiguration,
    IProviderReadinessEvaluator readinessEvaluator,
    IProviderOperationalStatusService providerOperationalStatusService,
    EgressUrlPolicy egressUrlPolicy,
    ILogger<ClientProviderConnectionsController> logger,
    IProviderActivationService? providerActivationService = null,
    ILicensingCapabilityService? licensingCapabilityService = null) : ControllerBase
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

    private ActionResult? ValidateSupportedAuthenticationConfiguration(ScmAuthenticationConfiguration candidate)
    {
        foreach (var (propertyName, message) in this.GetAuthenticationConfigurationErrors(candidate, egressUrlPolicy))
        {
            this.ModelState.AddModelError(propertyName, message);
        }

        return this.ModelState.ErrorCount == 0 ? null : this.ValidationProblem();
    }

    private void EnsureSupportedAuthenticationConfiguration(
        ScmAuthenticationConfiguration candidate,
        EgressUrlPolicy egressUrlPolicy)
    {
        var errors = this.GetAuthenticationConfigurationErrors(candidate, egressUrlPolicy)
            .Select(error => error.Message)
            .ToArray();

        if (errors.Length > 0)
        {
            throw new InvalidOperationException(string.Join(" ", errors));
        }
    }

    private IReadOnlyList<(string PropertyName, string Message)> GetAuthenticationConfigurationErrors(
        ScmAuthenticationConfiguration candidate, EgressUrlPolicy policy)
    {
        var errors = new List<(string PropertyName, string Message)>();
        if (candidate.ApplyHostBaseUrlEgressCheck &&
            CreateClientProviderConnectionRequestValidator.GetHostBaseUrlRefusal(policy, candidate.HostBaseUrl) is { } refusal)
        {
            errors.Add(("HostBaseUrl", refusal));
        }

        errors.AddRange(connectionConfiguration.Validate(candidate));
        return errors;
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

    private async Task<CapabilitySnapshot?> GetMultipleProviderCapabilityAsync(CancellationToken ct)
    {
        if (licensingCapabilityService is null)
        {
            return null;
        }

        return await licensingCapabilityService.GetCapabilityAsync(PremiumCapabilityKey.MultipleScmProviders, ct);
    }

    private async Task<ConnectionResponse> EnrichConnectionAsync(
        Guid clientId,
        ClientScmConnectionDto connection,
        CancellationToken ct)
    {
        var readiness = await readinessEvaluator.EvaluateAsync(clientId, connection, ct);
        return ConnectionResponse.FromApplication(ApplyReadiness(connection, readiness));
    }

    private async Task<IReadOnlyList<ConnectionResponse>> EnrichConnectionsAsync(
        Guid clientId,
        IReadOnlyList<ClientScmConnectionDto> connections,
        CancellationToken ct)
    {
        var enriched = new List<ConnectionResponse>(connections.Count);
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
    [HttpGet("provider-connections")]
    [ProducesResponseType(typeof(IReadOnlyList<ConnectionResponse>), StatusCodes.Status200OK)]
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
    [HttpGet("provider-connections/{connectionId:guid}")]
    [ProducesResponseType(typeof(ConnectionResponse), StatusCodes.Status200OK)]
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
    [HttpGet("provider-operations/status")]
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
    [HttpGet("provider-operations/audit-trail")]
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

    /// <summary>Creates a provider connection for a client. Azure DevOps Services supports OAuth client credentials and personal access tokens.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="request">Provider-connection details, including the per-connection retention opt-in settings.</param>
    /// <param name="validator">Validator for the request body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="201">Provider connection created.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">Client not found.</response>
    /// <response code="409">A provider connection already exists for the same provider family and host.</response>
    [HttpPost("provider-connections")]
    [ProducesResponseType(typeof(ConnectionResponse), StatusCodes.Status201Created)]
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

        var multipleProviderCapability = await this.GetMultipleProviderCapabilityAsync(ct);
        if (multipleProviderCapability is { IsAvailable: false })
        {
            var existingConnections = await connectionRepository.GetByClientIdAsync(clientId, ct);
            if (existingConnections.Count > 0)
            {
                return new PremiumFeatureUnavailableResult(multipleProviderCapability);
            }
        }

        var supportedAuthenticationValidation = this.ValidateSupportedAuthenticationConfiguration(
            new ScmAuthenticationConfiguration(
                request.ProviderFamily,
                request.HostBaseUrl,
                request.AuthenticationKind,
                request.UserName,
                request.OAuthTenantId,
                request.OAuthClientId,
                request.GitHubAppId,
                request.GitHubAppInstallationId));
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
                request.GitHubAppId,
                request.GitHubAppInstallationId,
                request.UserName,
                request.StoreThreads,
                request.StoreDiffs,
                request.RetentionDays,
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

    /// <summary>
    /// Applies partial updates to one provider connection. Changing authentication kind requires a replacement
    /// secret; Azure DevOps OAuth client-credentials authentication also requires tenant and client identifiers.
    /// </summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="connectionId">Provider-connection identifier.</param>
    /// <param name="request">Fields to update, including the per-connection retention opt-in settings; omit a field to leave it unchanged.</param>
    /// <param name="validator">Validator for the request body.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">Provider connection updated.</response>
    /// <response code="400">Validation failure.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">Client or provider connection not found.</response>
    /// <response code="409">A provider connection already exists for the same provider family and host.</response>
    [HttpPatch("provider-connections/{connectionId:guid}")]
    [ProducesResponseType(typeof(ConnectionResponse), StatusCodes.Status200OK)]
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

        if (request.IsActive == true && !existing.IsActive)
        {
            var capabilityConflict = await this.CheckMultipleProviderActivationCapabilityAsync(clientId, connectionId, ct);
            if (capabilityConflict is not null)
            {
                return capabilityConflict;
            }
        }

        var effective = ResolveEffectivePatchAuthentication(request, existing);
        var hostBaseUrlChanged = request.HostBaseUrl is not null
                                 && !string.Equals(request.HostBaseUrl, existing.HostBaseUrl, StringComparison.Ordinal);

        var supportedAuthenticationValidation = this.ValidateSupportedAuthenticationConfiguration(
            new ScmAuthenticationConfiguration(
                existing.ProviderFamily,
                request.HostBaseUrl ?? existing.HostBaseUrl,
                effective.AuthenticationKind,
                effective.CandidateUserName,
                effective.OAuthTenantId,
                effective.OAuthClientId,
                effective.AppId,
                effective.InstallationId,
                effective.HasCompatibleSecretMaterial,
                hostBaseUrlChanged));
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
                effective.AuthenticationKind,
                effective.OAuthTenantId,
                effective.OAuthClientId,
                request.DisplayName ?? existing.DisplayName,
                request.Secret,
                request.IsActive ?? existing.IsActive,
                effective.PersistedAppId,
                effective.PersistedInstallationId,
                effective.PersistedUserName,
                request.StoreThreads ?? existing.StoreThreads,
                request.StoreDiffs ?? existing.StoreDiffs,
                request.RetentionDays ?? existing.RetentionDays,
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

    private EffectiveScmAuthentication ResolveEffectivePatchAuthentication(PatchClientProviderConnectionRequest request, ClientScmConnectionDto existing) =>
        ProviderConnectionConfigurationService.ResolvePatchAuthentication(
            providerRegistry.GetConnectionConfigurationPolicy(existing.ProviderFamily), request.AuthenticationKind, request.UserName, request.OAuthTenantId,
            request.OAuthClientId, request.GitHubAppId, request.GitHubAppInstallationId, !string.IsNullOrWhiteSpace(request.Secret), existing);


    private async Task<IActionResult?> CheckMultipleProviderActivationCapabilityAsync(
        Guid clientId,
        Guid connectionId,
        CancellationToken ct)
    {
        var multipleProviderCapability = await this.GetMultipleProviderCapabilityAsync(ct);
        if (multipleProviderCapability is not { IsAvailable: false })
        {
            return null;
        }

        var existingConnections = await connectionRepository.GetByClientIdAsync(clientId, ct);
        return existingConnections.Any(connection => connection.Id != connectionId && connection.IsActive)
            ? new PremiumFeatureUnavailableResult(multipleProviderCapability)
            : null;
    }

    /// <summary>Deletes one provider connection from a client.</summary>
    /// <param name="clientId">Client identifier.</param>
    /// <param name="connectionId">Provider-connection identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">Provider connection deleted.</response>
    /// <response code="401">Missing or invalid credentials.</response>
    /// <response code="403">Caller lacks required client access.</response>
    /// <response code="404">Client or provider connection not found.</response>
    [HttpDelete("provider-connections/{connectionId:guid}")]
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
    [HttpPost("provider-connections/{connectionId:guid}/verify")]
    [ProducesResponseType(typeof(ConnectionResponse), StatusCodes.Status200OK)]
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
            this.EnsureSupportedAuthenticationConfiguration(
                new ScmAuthenticationConfiguration(
                    connection.ProviderFamily,
                    connection.HostBaseUrl,
                    connection.AuthenticationKind,
                    connection.UserName,
                    connection.OAuthTenantId,
                    connection.OAuthClientId,
                    connection.AppId,
                    connection.InstallationId),
                egressUrlPolicy);

            await connectionConfiguration.VerifyAsync(connection, ct);
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
    [property: JsonRequired] ScmProvider ProviderFamily,
    string HostBaseUrl,
    [property: JsonRequired] ScmAuthenticationKind AuthenticationKind,
    string? UserName,
    string? OAuthTenantId,
    string? OAuthClientId,
    string DisplayName,
    string Secret,
    bool IsActive = true,
    long? GitHubAppId = null,
    long? GitHubAppInstallationId = null,
    bool StoreThreads = false,
    bool StoreDiffs = false,
    int? RetentionDays = null)
{
    /// <summary>Renders the request without the secret; see <see cref="SecretSafeRendering" />.</summary>
    public override string ToString()
    {
        return $"{nameof(CreateClientProviderConnectionRequest)} {{ ProviderFamily = {this.ProviderFamily}, "
               + $"HostBaseUrl = {SecretSafeRendering.Address(this.HostBaseUrl)}, AuthenticationKind = {this.AuthenticationKind}, "
               + $"UserName = {this.UserName}, DisplayName = {this.DisplayName}, "
               + $"Secret = {SecretSafeRendering.Elide(this.Secret)}, IsActive = {this.IsActive} }}";
    }
}

/// <summary>Request body for patching a client-scoped provider connection.</summary>
public sealed record PatchClientProviderConnectionRequest(
    string? HostBaseUrl = null,
    ScmAuthenticationKind? AuthenticationKind = null,
    string? UserName = null,
    string? OAuthTenantId = null,
    string? OAuthClientId = null,
    string? DisplayName = null,
    string? Secret = null,
    bool? IsActive = null,
    long? GitHubAppId = null,
    long? GitHubAppInstallationId = null,
    bool? StoreThreads = null,
    bool? StoreDiffs = null,
    int? RetentionDays = null)
{
    /// <summary>Renders the request without the secret; see <see cref="SecretSafeRendering" />.</summary>
    public override string ToString()
    {
        return $"{nameof(PatchClientProviderConnectionRequest)} {{ HostBaseUrl = {SecretSafeRendering.Address(this.HostBaseUrl)}, "
               + $"AuthenticationKind = {this.AuthenticationKind}, UserName = {this.UserName}, "
               + $"DisplayName = {this.DisplayName}, Secret = {SecretSafeRendering.Elide(this.Secret)}, "
               + $"IsActive = {this.IsActive} }}";
    }
}
