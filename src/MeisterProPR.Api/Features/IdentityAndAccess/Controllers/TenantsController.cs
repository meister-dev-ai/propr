// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using FluentValidation;
using FluentValidation.Results;
using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>Administrative tenant endpoints for platform and tenant administrators.</summary>
[ApiController]
public sealed class TenantsController(ITenantAdminService tenantAdminService) : ControllerBase
{
    /// <summary>Lists tenants visible to the current caller.</summary>
    [HttpGet("/api/admin/tenants")]
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
    [HttpGet("/api/admin/tenants/{tenantId:guid}")]
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
    [HttpPost("/api/admin/tenants")]
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
    [HttpPatch("/api/admin/tenants/{tenantId:guid}")]
    [ProducesResponseType(typeof(TenantDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PatchTenant(
        Guid tenantId,
        [FromBody] UpdateTenantRequest request,
        [FromServices] IValidator<UpdateTenantRequest> validator,
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

        try
        {
            var updated = await tenantAdminService.PatchAsync(
                tenantId,
                request.DisplayName,
                request.IsActive,
                request.LocalLoginEnabled,
                ct);

            return updated is null ? this.NotFound() : this.Ok(updated);
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

/// <summary>Create-tenant request payload.</summary>
public sealed record CreateTenantRequest(string Slug, string DisplayName);

/// <summary>Patch-tenant request payload.</summary>
public sealed record UpdateTenantRequest(string? DisplayName, bool? IsActive, bool? LocalLoginEnabled);
