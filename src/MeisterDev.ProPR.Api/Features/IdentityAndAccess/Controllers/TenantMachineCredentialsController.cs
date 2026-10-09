// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.IdentityAndAccess.Authentication.Models;
using MeisterDev.ProPR.Api.Features.IdentityAndAccess.Authentication;
using MeisterDev.ProPR.Web;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>Operator-managed credentials for hosted services assigned to one tenant.</summary>
[ApiController]
[Route("admin/tenants/{tenantId:guid}/machine-credentials")]
public sealed class TenantMachineCredentialsController(TenantMachineCredentialService credentials) : ControllerBase
{
    /// <summary>Issues a credential and returns its secret once to the platform administrator.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(IssuedTenantMachineCredential), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Issue(Guid tenantId, [FromBody] IssueTenantMachineCredentialRequest request, CancellationToken ct)
    {
        var denial = AuthHelpers.RequirePlatformAdmin(this.HttpContext);
        if (denial is not null)
        {
            return denial;
        }

        if (AuthHelpers.GetUserId(this.HttpContext) is not Guid actorUserId)
        {
            return this.Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(request.Label) || request.Label.Length > 128 ||
            request.ExpiresAt is not null && request.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return this.BadRequest(new { error = "Provide a label of at most 128 characters and a future expiry." });
        }

        IssuedTenantMachineCredential? issued;
        try
        {
            issued = await credentials.IssueAsync(tenantId, request.Label.Trim(), request.ExpiresAt, actorUserId, ct);
        }
        catch (ArgumentOutOfRangeException)
        {
            return this.BadRequest(new { error = "Expiry must be in the future." });
        }

        this.Response.Headers.CacheControl = "no-store";
        return issued is null ? this.NotFound() : this.StatusCode(StatusCodes.Status201Created, issued);
    }

    /// <summary>Lists credential metadata without token hashes or secret material.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<TenantMachineCredentialSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(Guid tenantId, CancellationToken ct)
    {
        var denial = AuthHelpers.RequirePlatformAdmin(this.HttpContext);
        return denial ?? this.Ok(await credentials.ListAsync(tenantId, ct));
    }

    /// <summary>Revokes a credential so subsequent requests cannot authenticate.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Revoke(Guid tenantId, Guid id, CancellationToken ct)
    {
        var denial = AuthHelpers.RequirePlatformAdmin(this.HttpContext);
        if (denial is not null)
        {
            return denial;
        }

        if (AuthHelpers.GetUserId(this.HttpContext) is not Guid actorUserId)
        {
            return this.Unauthorized();
        }

        return await credentials.RevokeAsync(tenantId, id, actorUserId, ct)
            ? this.NoContent()
            : this.NotFound();
    }
}

/// <summary>Metadata required to issue a tenant machine credential.</summary>
public sealed record IssueTenantMachineCredentialRequest(string Label, DateTimeOffset? ExpiresAt);
