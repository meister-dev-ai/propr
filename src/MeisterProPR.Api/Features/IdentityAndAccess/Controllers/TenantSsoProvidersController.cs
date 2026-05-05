// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using FluentValidation.Results;
using MeisterProPR.Api.Extensions;
using MeisterProPR.Api.Features.Licensing;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;
using MeisterProPR.Application.Features.Licensing.Support;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Administrative CRUD endpoints for tenant-owned SSO providers.</summary>
[ApiController]
public sealed class TenantSsoProvidersController(
    ITenantAdminService tenantAdminService,
    ITenantSsoProviderService tenantSsoProviderService,
    ILogger<TenantSsoProvidersController> logger,
    ILicensingCapabilityService? licensingCapabilityService = null) : ControllerBase
{
    private async Task<IActionResult?> RequireTenantSsoCapabilityAsync(CancellationToken ct)
    {
        var capability = await LicensingCapabilityGuard.GetUnavailableCapabilityAsync(
            licensingCapabilityService,
            PremiumCapabilityKey.SsoAuthentication,
            ct);

        return capability is null ? null : new PremiumFeatureUnavailableResult(capability);
    }

    /// <summary>Lists tenant-owned SSO providers for one tenant administrator view.</summary>
    [HttpGet("/api/admin/tenants/{tenantId:guid}/sso-providers")]
    [ProducesResponseType(typeof(IReadOnlyList<TenantSsoProviderDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PremiumFeatureUnavailablePayload), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> GetProviders(Guid tenantId, CancellationToken ct)
    {
        var auth = AuthHelpers.RequireTenantRole(this.HttpContext, tenantId, TenantRole.TenantAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        var capability = await this.RequireTenantSsoCapabilityAsync(ct);
        if (capability is not null)
        {
            return capability;
        }

        if (!await tenantAdminService.ExistsAsync(tenantId, ct))
        {
            return this.NotFound();
        }

        var providers = await tenantSsoProviderService.ListAsync(tenantId, ct);
        logger.LogInformation(
            "TenantSsoProvidersListed TenantId={TenantId} ProviderCount={ProviderCount}",
            tenantId,
            providers.Count);
        return this.Ok(providers);
    }

    /// <summary>Returns a single tenant-owned SSO provider configuration.</summary>
    [HttpGet("/api/admin/tenants/{tenantId:guid}/sso-providers/{providerId:guid}")]
    [ProducesResponseType(typeof(TenantSsoProviderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PremiumFeatureUnavailablePayload), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> GetProvider(Guid tenantId, Guid providerId, CancellationToken ct)
    {
        var auth = AuthHelpers.RequireTenantRole(this.HttpContext, tenantId, TenantRole.TenantAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        var capability = await this.RequireTenantSsoCapabilityAsync(ct);
        if (capability is not null)
        {
            return capability;
        }

        var provider = await tenantSsoProviderService.GetByIdAsync(tenantId, providerId, ct);
        if (provider is not null)
        {
            logger.LogInformation(
                "TenantSsoProviderRead TenantId={TenantId} ProviderId={ProviderId}",
                tenantId,
                providerId);
        }

        return provider is null ? this.NotFound() : this.Ok(provider);
    }

    /// <summary>Creates a tenant-owned SSO provider.</summary>
    [HttpPost("/api/admin/tenants/{tenantId:guid}/sso-providers")]
    [ProducesResponseType(typeof(TenantSsoProviderDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PremiumFeatureUnavailablePayload), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateProvider(
        Guid tenantId,
        [FromBody] CreateTenantSsoProviderRequest request,
        [FromServices] IValidator<CreateTenantSsoProviderRequest> validator,
        CancellationToken ct)
    {
        var auth = AuthHelpers.RequireTenantRole(this.HttpContext, tenantId, TenantRole.TenantAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        var capability = await this.RequireTenantSsoCapabilityAsync(ct);
        if (capability is not null)
        {
            return capability;
        }

        var validation = this.ValidateRequest(await validator.ValidateAsync(request, ct));
        if (validation is not null)
        {
            return validation;
        }

        if (!await tenantAdminService.ExistsAsync(tenantId, ct))
        {
            return this.NotFound();
        }

        try
        {
            var created = await tenantSsoProviderService.CreateAsync(
                tenantId,
                request.DisplayName,
                request.ProviderKind,
                request.ProtocolKind,
                request.IssuerOrAuthorityUrl,
                request.ClientId,
                request.ClientSecret,
                request.Scopes,
                request.AllowedEmailDomains,
                request.IsEnabled,
                request.AutoCreateUsers,
                ct);

            var safeProviderKind = created.ProviderKind.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

            logger.LogInformation(
                "TenantSsoProviderCreated TenantId={TenantId} ProviderId={ProviderId} ProviderKind={ProviderKind}",
                tenantId,
                created.Id,
                safeProviderKind);

            return this.CreatedAtAction(nameof(this.GetProvider), new { tenantId, providerId = created.Id }, created);
        }
        catch (InvalidOperationException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
    }

    /// <summary>Replaces a tenant-owned SSO provider configuration.</summary>
    [HttpPut("/api/admin/tenants/{tenantId:guid}/sso-providers/{providerId:guid}")]
    [ProducesResponseType(typeof(TenantSsoProviderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PremiumFeatureUnavailablePayload), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateProvider(
        Guid tenantId,
        Guid providerId,
        [FromBody] UpdateTenantSsoProviderRequest request,
        [FromServices] IValidator<UpdateTenantSsoProviderRequest> validator,
        CancellationToken ct)
    {
        var auth = AuthHelpers.RequireTenantRole(this.HttpContext, tenantId, TenantRole.TenantAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        var capability = await this.RequireTenantSsoCapabilityAsync(ct);
        if (capability is not null)
        {
            return capability;
        }

        var validation = this.ValidateRequest(await validator.ValidateAsync(request, ct));
        if (validation is not null)
        {
            return validation;
        }

        try
        {
            var updated = await tenantSsoProviderService.UpdateAsync(
                tenantId,
                providerId,
                request.DisplayName,
                request.ProviderKind,
                request.ProtocolKind,
                request.IssuerOrAuthorityUrl,
                request.ClientId,
                request.ClientSecret,
                request.Scopes,
                request.AllowedEmailDomains,
                request.IsEnabled,
                request.AutoCreateUsers,
                ct);

            if (updated is not null)
            {
                logger.LogInformation(
                    "TenantSsoProviderUpdated TenantId={TenantId} ProviderId={ProviderId} IsEnabled={IsEnabled}",
                    tenantId,
                    providerId,
                    updated.IsEnabled);
            }

            return updated is null ? this.NotFound() : this.Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
    }

    /// <summary>Deletes a tenant-owned SSO provider.</summary>
    [HttpDelete("/api/admin/tenants/{tenantId:guid}/sso-providers/{providerId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(PremiumFeatureUnavailablePayload), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteProvider(Guid tenantId, Guid providerId, CancellationToken ct)
    {
        var auth = AuthHelpers.RequireTenantRole(this.HttpContext, tenantId, TenantRole.TenantAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        var capability = await this.RequireTenantSsoCapabilityAsync(ct);
        if (capability is not null)
        {
            return capability;
        }

        try
        {
            var deleted = await tenantSsoProviderService.DeleteAsync(tenantId, providerId, ct);
            if (deleted)
            {
                logger.LogWarning(
                    "TenantSsoProviderDeleted TenantId={TenantId} ProviderId={ProviderId}",
                    tenantId,
                    providerId);
            }

            return deleted ? this.NoContent() : this.NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
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
}

/// <summary>Create-tenant-provider request payload.</summary>
public record CreateTenantSsoProviderRequest(
    string DisplayName,
    string ProviderKind,
    string ProtocolKind,
    string? IssuerOrAuthorityUrl,
    string ClientId,
    string? ClientSecret,
    IEnumerable<string>? Scopes,
    IEnumerable<string>? AllowedEmailDomains,
    bool IsEnabled,
    bool AutoCreateUsers);

/// <summary>Replace-tenant-provider request payload.</summary>
public sealed record UpdateTenantSsoProviderRequest(
    string DisplayName,
    string ProviderKind,
    string ProtocolKind,
    string? IssuerOrAuthorityUrl,
    string ClientId,
    string? ClientSecret,
    IEnumerable<string>? Scopes,
    IEnumerable<string>? AllowedEmailDomains,
    bool IsEnabled,
    bool AutoCreateUsers)
    : CreateTenantSsoProviderRequest(
        DisplayName,
        ProviderKind,
        ProtocolKind,
        IssuerOrAuthorityUrl,
        ClientId,
        ClientSecret,
        Scopes,
        AllowedEmailDomains,
        IsEnabled,
        AutoCreateUsers);
