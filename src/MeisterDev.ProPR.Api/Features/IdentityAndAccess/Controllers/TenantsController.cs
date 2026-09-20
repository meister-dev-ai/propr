// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using FluentValidation;
using FluentValidation.Results;
using MeisterDev.ProPR.Api.Extensions;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using MeisterDev.ProPR.Web;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>Administrative tenant endpoints for platform and tenant administrators.</summary>
[ApiController]
[Route("admin/tenants")]
public sealed class TenantsController(ITenantAdminService tenantAdminService) : ControllerBase
{
    /// <summary>Lists tenants visible to the current caller.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<TenantDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> ListTenants(CancellationToken ct)
    {
        if (AuthHelpers.IsAdmin(this.HttpContext))
        {
            return this.Ok(await tenantAdminService.GetAllAsync(ct));
        }

        var auth = AuthHelpers.RequireAnyTenantRole(this.HttpContext, TenantRole.TenantUser);
        if (auth is not null)
        {
            return auth;
        }

        var visibleTenants = new List<TenantDto>();
        foreach (var tenantId in AuthHelpers.GetTenantRoles(this.HttpContext).Keys)
        {
            var tenant = await tenantAdminService.GetByIdAsync(tenantId, ct);
            if (tenant is not null)
            {
                visibleTenants.Add(tenant);
            }
        }

        return this.Ok(visibleTenants);
    }

    /// <summary>Returns one tenant when the caller belongs to it or is a platform administrator.</summary>
    [HttpGet("{tenantId:guid}")]
    [ProducesResponseType(typeof(TenantDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTenant(Guid tenantId, CancellationToken ct)
    {
        var auth = AuthHelpers.RequireTenantRole(this.HttpContext, tenantId, TenantRole.TenantUser);
        if (auth is not null)
        {
            return auth;
        }

        var tenant = await tenantAdminService.GetByIdAsync(tenantId, ct);
        return tenant is null ? this.NotFound() : this.Ok(tenant);
    }

    /// <summary>Creates a new tenant boundary.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(TenantDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateTenant(
        [FromBody] CreateTenantRequest request,
        [FromServices] IValidator<CreateTenantRequest> validator,
        CancellationToken ct)
    {
        var auth = AuthHelpers.RequireAdmin(this.HttpContext);
        if (auth is not null)
        {
            return auth;
        }

        var validation = this.ValidateRequest(await validator.ValidateAsync(request, ct));
        if (validation is not null)
        {
            return validation;
        }

        var existing = await tenantAdminService.GetBySlugAsync(request.Slug, ct);
        if (existing is not null)
        {
            return this.Conflict(new { error = "A tenant with that slug already exists." });
        }

        try
        {
            var created = await tenantAdminService.CreateAsync(request.Slug, request.DisplayName, ct: ct);
            return this.CreatedAtAction(nameof(this.GetTenant), new { tenantId = created.Id }, created);
        }
        catch (InvalidOperationException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
    }

    /// <summary>Applies partial tenant policy updates.</summary>
    [HttpPatch("{tenantId:guid}")]
    [ProducesResponseType(typeof(TenantDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PatchTenant(
        Guid tenantId,
        [FromBody] UpdateTenantRequest request,
        [FromServices] IValidator<UpdateTenantRequest> validator,
        [FromServices] IAiProviderDriverRegistry providerDrivers,
        CancellationToken ct)
    {
        var auth = AuthHelpers.RequireTenantRole(this.HttpContext, tenantId, TenantRole.TenantAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        var validation = this.ValidateRequest(await validator.ValidateAsync(request, ct));
        if (validation is not null)
        {
            return validation;
        }

        if (this.RefuseUnclaimedProviders(request.AllowedAiProviderKinds, providerDrivers) is { } unclaimed)
        {
            return unclaimed;
        }

        try
        {
            var updated = await tenantAdminService.PatchAsync(
                tenantId,
                request.DisplayName,
                request.IsActive,
                request.LocalLoginEnabled,
                request.AllowedAiProviderKinds,
                request.AllowedAiEndpointHosts,
                request.RemovedUnresolvedAiProviderKinds,
                ct);

            return updated is null ? this.NotFound() : this.Ok(updated);
        }
        catch (InvalidOperationException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
    }

    // A permitted-family entry names a family by its identity key, and an entry no loaded family claims permits
    // nothing. Refused here, naming the entry and what is available, so a mistyped key is corrected on the form
    // rather than stored and then refusing every provider the tenant has. An entry already stored that stopped
    // resolving is a different case: it stays on the policy, is reported on the tenant, and is removed by naming
    // it for removal.
    private IActionResult? RefuseUnclaimedProviders(
        IReadOnlyList<string>? requested,
        IAiProviderDriverRegistry providerDrivers)
    {
        if (requested is not { Count: > 0 })
        {
            return null;
        }

        var unclaimed = requested
            .Where(entry => !providerDrivers.IsRegistered(entry))
            .ToList();

        if (unclaimed.Count == 0)
        {
            return null;
        }

        this.ModelState.AddModelError(
            "allowedAiProviderKinds",
            $"No installed provider family claims {string.Join(", ", unclaimed.Select(entry => $"'{entry}'"))} "
            + $"(available: {string.Join(", ", providerDrivers.RegisteredKinds)}).");
        return this.ValidationProblem();
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

/// <summary>Create-tenant request payload.</summary>
public sealed record CreateTenantRequest(string Slug, string DisplayName);

/// <summary>Patch-tenant request payload.</summary>
/// <param name="DisplayName">New display name, or null to leave unchanged.</param>
/// <param name="IsActive">New active state, or null to leave unchanged.</param>
/// <param name="LocalLoginEnabled">New local-login policy, or null to leave unchanged.</param>
/// <param name="AllowedAiProviderKinds">
///     Provider families this tenant's clients may use, by identity key, or null to leave unchanged. An empty
///     list clears the restriction back to unrestricted. An entry no loaded family claims is refused, so a
///     mistyped key is reported on the form instead of leaving the tenant permitting nothing.
/// </param>
/// <param name="AllowedAiEndpointHosts">
///     Endpoint hosts this tenant's clients may reach, or null to leave unchanged. An empty list clears the
///     restriction. An entry matches a host exactly, or any subdomain when written with a leading dot.
/// </param>
/// <param name="RemovedUnresolvedAiProviderKinds">
///     Permitted-family entries no loaded family claims, to remove from the policy. They are reported on the
///     tenant as <c>unresolvedAiProviderKinds</c>, and <paramref name="AllowedAiProviderKinds" /> carries only
///     entries a loaded family claims, so naming one here is how it is removed. Anything not named here survives
///     the write, so a removal cannot happen as a side effect of saving the families. An entry the tenant does
///     not hold is ignored.
/// </param>
public sealed record UpdateTenantRequest(
    string? DisplayName,
    bool? IsActive,
    bool? LocalLoginEnabled,
    IReadOnlyList<string>? AllowedAiProviderKinds = null,
    IReadOnlyList<string>? AllowedAiEndpointHosts = null,
    IReadOnlyList<string>? RemovedUnresolvedAiProviderKinds = null);
