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

/// <summary>Administrative tenant membership endpoints scoped to a single tenant.</summary>
[ApiController]
public sealed class TenantMembershipsController(
    ITenantAdminService tenantAdminService,
    ITenantMembershipService tenantMembershipService) : ControllerBase
{
    /// <summary>Lists tenant memberships for one tenant administrator scope.</summary>
    [HttpGet("/api/admin/tenants/{tenantId:guid}/memberships")]
    [ProducesResponseType(typeof(IReadOnlyList<TenantMembershipDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListMemberships(Guid tenantId, CancellationToken ct)
    {
        var auth = AuthHelpers.RequireTenantRole(this.HttpContext, tenantId, TenantRole.TenantAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        if (!await tenantAdminService.ExistsAsync(tenantId, ct))
        {
            return this.NotFound();
        }

        return this.Ok(await tenantMembershipService.ListAsync(tenantId, ct));
    }

    /// <summary>Returns one tenant membership.</summary>
    [HttpGet("/api/admin/tenants/{tenantId:guid}/memberships/{membershipId:guid}")]
    [ProducesResponseType(typeof(TenantMembershipDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMembership(Guid tenantId, Guid membershipId, CancellationToken ct)
    {
        var auth = AuthHelpers.RequireTenantRole(this.HttpContext, tenantId, TenantRole.TenantAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        var membership = await tenantMembershipService.GetByIdAsync(tenantId, membershipId, ct);
        return membership is null ? this.NotFound() : this.Ok(membership);
    }

    /// <summary>Updates the role for an existing tenant membership.</summary>
    [HttpPatch("/api/admin/tenants/{tenantId:guid}/memberships/{membershipId:guid}")]
    [ProducesResponseType(typeof(TenantMembershipDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> PatchMembership(
        Guid tenantId,
        Guid membershipId,
        [FromBody] UpdateTenantMembershipRequest request,
        [FromServices] IValidator<UpdateTenantMembershipRequest> validator,
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
            var membership = await tenantMembershipService.PatchAsync(tenantId, membershipId, ParseRole(request.Role), ct);
            return membership is null ? this.NotFound() : this.Ok(membership);
        }
        catch (InvalidOperationException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
    }

    /// <summary>Removes a tenant membership when recovery-safe rules allow it.</summary>
    [HttpDelete("/api/admin/tenants/{tenantId:guid}/memberships/{membershipId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteMembership(Guid tenantId, Guid membershipId, CancellationToken ct)
    {
        var auth = AuthHelpers.RequireTenantRole(this.HttpContext, tenantId, TenantRole.TenantAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        try
        {
            return await tenantMembershipService.DeleteAsync(tenantId, membershipId, ct)
                ? this.NoContent()
                : this.NotFound();
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

    private static TenantRole ParseRole(string role)
    {
        return Enum.Parse<TenantRole>(role, true);
    }
}

/// <summary>Tenant membership role update payload.</summary>
public sealed record UpdateTenantMembershipRequest(string Role);
