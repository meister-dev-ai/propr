// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json.Serialization;
using MeisterProPR.Api.Extensions;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.AspNetCore.Mvc;

namespace MeisterProPR.Api.Controllers;

/// <summary>
///     Tenant-administrator endpoints for managing which of a tenant's members can access which of the
///     tenant's clients. All operations are scoped to a single tenant and require the tenant administrator role.
/// </summary>
[ApiController]
[Route("admin/tenants/{tenantId:guid}")]
public sealed class TenantMemberClientAccessController(
    ITenantAdminService tenantAdminService,
    ITenantMemberClientAccessService memberClientAccessService) : ControllerBase
{
    /// <summary>Lists the clients that belong to the tenant, for populating member client-access pickers.</summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The tenant's clients.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller is not an administrator of this tenant.</response>
    /// <response code="404">The tenant does not exist.</response>
    [HttpGet("clients")]
    [ProducesResponseType(typeof(IReadOnlyList<TenantClientSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListTenantClients(Guid tenantId, CancellationToken ct)
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

        return this.Ok(await memberClientAccessService.ListTenantClientsAsync(tenantId, ct));
    }

    /// <summary>Lists a member's client-access assignments within the tenant.</summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="membershipId">Membership identifier of the tenant member.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The member's client-access assignments.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller is not an administrator of this tenant.</response>
    /// <response code="404">The membership does not exist within the tenant.</response>
    [HttpGet("memberships/{membershipId:guid}/clients")]
    [ProducesResponseType(typeof(IReadOnlyList<TenantMemberClientAccessDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListMemberClientAccess(Guid tenantId, Guid membershipId, CancellationToken ct)
    {
        var auth = AuthHelpers.RequireTenantRole(this.HttpContext, tenantId, TenantRole.TenantAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        var assignments = await memberClientAccessService.ListMemberAccessAsync(tenantId, membershipId, ct);
        return assignments is null ? this.NotFound() : this.Ok(assignments);
    }

    /// <summary>Grants (or updates) a member's role on a client within the tenant.</summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="membershipId">Membership identifier of the tenant member.</param>
    /// <param name="request">The client and role to grant.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The resulting client-access assignment.</response>
    /// <response code="400">The target client does not belong to this tenant.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller is not an administrator of this tenant.</response>
    /// <response code="404">The membership does not exist within the tenant.</response>
    /// <response code="409">The tenant cannot be modified (System tenant).</response>
    [HttpPost("memberships/{membershipId:guid}/clients")]
    [ProducesResponseType(typeof(TenantMemberClientAccessDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AssignMemberClientAccess(
        Guid tenantId,
        Guid membershipId,
        [FromBody] AssignMemberClientAccessRequest request,
        CancellationToken ct)
    {
        var auth = AuthHelpers.RequireTenantRole(this.HttpContext, tenantId, TenantRole.TenantAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        try
        {
            var result = await memberClientAccessService.AssignAsync(
                tenantId,
                membershipId,
                request.ClientId,
                request.Role,
                ct);

            return result.Outcome switch
            {
                TenantMemberClientAccessOutcome.MembershipNotFound => this.NotFound(),
                TenantMemberClientAccessOutcome.ClientNotInTenant => this.BadRequest(new { error = "The specified client does not belong to this tenant." }),
                _ => this.Ok(result.Assignment),
            };
        }
        catch (InvalidOperationException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
    }

    /// <summary>Revokes a member's access to a client within the tenant.</summary>
    /// <param name="tenantId">Tenant identifier.</param>
    /// <param name="membershipId">Membership identifier of the tenant member.</param>
    /// <param name="clientId">Client identifier to revoke.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="204">Access was revoked (idempotent).</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller is not an administrator of this tenant.</response>
    /// <response code="404">The membership does not exist within the tenant.</response>
    /// <response code="409">The tenant cannot be modified (System tenant).</response>
    [HttpDelete("memberships/{membershipId:guid}/clients/{clientId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveMemberClientAccess(
        Guid tenantId,
        Guid membershipId,
        Guid clientId,
        CancellationToken ct)
    {
        var auth = AuthHelpers.RequireTenantRole(this.HttpContext, tenantId, TenantRole.TenantAdministrator);
        if (auth is not null)
        {
            return auth;
        }

        try
        {
            var outcome = await memberClientAccessService.RemoveAsync(tenantId, membershipId, clientId, ct);
            return outcome == TenantMemberClientAccessOutcome.MembershipNotFound ? this.NotFound() : this.NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return this.Conflict(new { error = ex.Message });
        }
    }
}

/// <summary>Request payload to grant a tenant member a role on a client.</summary>
public sealed record AssignMemberClientAccessRequest(
    [property: JsonRequired] Guid ClientId,
    [property: JsonRequired] ClientRole Role);
