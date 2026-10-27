// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Text.Json.Serialization;
using MeisterDev.ProPR.Api.Extensions;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Web;
using Microsoft.AspNetCore.Mvc;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>
///     The provisioning of the users of a hosting platform. The platform creates the user of a person who has an account
///     at the identity provider for provisioned users, and it sets the memberships of the user in the tenants of the
///     installation. The person signs in through that provider at <c>/auth/provisioned-user/challenge</c>. Platform
///     administrators only; every operation is idempotent.
/// </summary>
[ApiController]
[Route("admin")]
[TenantData(TenantDataAccess.Metadata)]
public sealed class ProvisionedUsersController(IProvisionedUserService provisionedUsers) : ControllerBase
{
    /// <summary>
    ///     Creates the user of a person at the identity provider for provisioned users, or updates its email address and
    ///     display name. ProPR creates the account without a password and keeps it inactive until the user holds a
    ///     membership.
    /// </summary>
    /// <param name="subject">The subject of the person at the provider, as the <c>sub</c> claim of its ID token carries it.</param>
    /// <param name="request">The email address and the display name of the person.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <response code="200">The user existed; ProPR applied the email address and the display name.</response>
    /// <response code="201">ProPR created the user.</response>
    /// <response code="400">The subject, the email address or the display name is not accepted.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller is not a platform administrator.</response>
    /// <response code="404">The installation configured no identity provider for provisioned users.</response>
    /// <response code="409">The account of the subject holds platform administrator rights.</response>
    [HttpPut("provisioned-users/{subject}")]
    [ProducesResponseType(typeof(ProvisionedUserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProvisionedUserDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> PutUser(string? subject, [FromBody] ProvisionedUserRequest request, CancellationToken ct)
    {
        if (AuthHelpers.RequirePlatformAdmin(this.HttpContext) is { } refused)
        {
            return refused;
        }

        // A subject of white space reaches the action as null, and the service refuses it with the others it does not
        // accept.
        var result = await provisionedUsers.PutUserAsync(
            subject ?? string.Empty,
            request.Email,
            request.DisplayName,
            AuthHelpers.GetUserId(this.HttpContext),
            ct);
        return result.Outcome == ProvisioningOutcome.Created
            ? this.StatusCode(StatusCodes.Status201Created, result.Value)
            : this.Answer(result);
    }

    /// <summary>Lists the provisioned users that hold a membership in the tenant, with their clients of the tenant.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <response code="200">The provisioned members of the tenant, ordered by subject.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller is not a platform administrator.</response>
    /// <response code="404">The tenant does not exist, or the installation configured no identity provider for provisioned users.</response>
    [HttpGet("tenants/{tenantId:guid}/provisioned-members")]
    [ProducesResponseType(typeof(IReadOnlyList<ProvisionedMemberDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListMembers(Guid tenantId, CancellationToken ct)
    {
        if (AuthHelpers.RequirePlatformAdmin(this.HttpContext) is { } refused)
        {
            return refused;
        }

        return this.Answer(await provisionedUsers.ListMembersAsync(tenantId, ct));
    }

    /// <summary>
    ///     Sets the membership of a provisioned user in the tenant: the tenant role <c>TenantUser</c> and the role
    ///     <c>ClientUser</c> on exactly the clients of the request. ProPR removes the roles of the user on the other
    ///     clients of the tenant, and enables a user that it kept inactive for the lack of a membership.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="subject">The subject of the person at the identity provider for provisioned users.</param>
    /// <param name="request">The clients of the tenant that the user reads.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <response code="200">The membership as ProPR stored it.</response>
    /// <response code="400">The subject is not accepted, or a client does not belong to the tenant.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller is not a platform administrator.</response>
    /// <response code="404">The tenant or the user does not exist, or the installation configured no identity provider for provisioned users.</response>
    /// <response code="409">The tenant is the System tenant, or the account of the subject holds platform administrator rights.</response>
    [HttpPut("tenants/{tenantId:guid}/provisioned-members/{subject}")]
    [ProducesResponseType(typeof(ProvisionedMemberDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> PutMembership(
        Guid tenantId,
        string? subject,
        [FromBody] ProvisionedMembershipRequest request,
        CancellationToken ct)
    {
        if (AuthHelpers.RequirePlatformAdmin(this.HttpContext) is { } refused)
        {
            return refused;
        }

        return this.Answer(
            await provisionedUsers.PutMembershipAsync(
                tenantId,
                subject ?? string.Empty,
                request.ClientIds,
                AuthHelpers.GetUserId(this.HttpContext),
                ct));
    }

    /// <summary>
    ///     Removes the membership of a provisioned user in the tenant with the roles of the user on the clients of the
    ///     tenant. When the user holds no membership any more, ProPR disables the user and revokes its refresh tokens and
    ///     personal access tokens.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="subject">The subject of the person at the identity provider for provisioned users.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    /// <response code="204">The user holds no membership in the tenant, also when it held none before or does not exist.</response>
    /// <response code="400">The subject is not accepted.</response>
    /// <response code="401">The caller is not authenticated.</response>
    /// <response code="403">The caller is not a platform administrator.</response>
    /// <response code="404">The tenant does not exist, or the installation configured no identity provider for provisioned users.</response>
    /// <response code="409">The tenant is the System tenant, or the account of the subject holds platform administrator rights.</response>
    [HttpDelete("tenants/{tenantId:guid}/provisioned-members/{subject}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteMembership(Guid tenantId, string? subject, CancellationToken ct)
    {
        if (AuthHelpers.RequirePlatformAdmin(this.HttpContext) is { } refused)
        {
            return refused;
        }

        var result = await provisionedUsers.RemoveMembershipAsync(tenantId, subject ?? string.Empty, AuthHelpers.GetUserId(this.HttpContext), ct);
        return result.IsSuccess ? this.NoContent() : this.Answer(result);
    }

    private IActionResult Answer<T>(ProvisioningResult<T> result)
    {
        return result.Outcome switch
        {
            ProvisioningOutcome.Succeeded or ProvisioningOutcome.Created => this.Ok(result.Value),
            ProvisioningOutcome.ProviderNotConfigured => this.NotFound(new { error = "provisioned_sign_in_not_configured", message = result.Message }),
            ProvisioningOutcome.TenantNotFound => this.NotFound(new { error = "tenant_not_found", message = "The tenant does not exist." }),
            ProvisioningOutcome.UserNotFound => this.NotFound(new { error = "provisioned_user_not_found", message = result.Message }),
            ProvisioningOutcome.InvalidRequest => this.BadRequest(new { error = "invalid_request", message = result.Message }),
            ProvisioningOutcome.ClientNotInTenant => this.BadRequest(
                new { error = "client_not_in_tenant", message = result.Message, clientIds = result.UnknownClientIds }),
            ProvisioningOutcome.SystemTenant => this.Conflict(new { error = "system_tenant", message = result.Message }),
            ProvisioningOutcome.PlatformAdministrator => this.Conflict(new { error = "platform_administrator", message = result.Message }),
            _ => throw new InvalidOperationException($"The provisioning outcome {result.Outcome} has no answer."),
        };
    }
}

/// <summary>The person behind a provisioned user, as the hosting platform knows it.</summary>
/// <param name="Email">The email address of the person.</param>
/// <param name="DisplayName">The display name of the person, or <see langword="null" />.</param>
public sealed record ProvisionedUserRequest([property: JsonRequired] string Email, string? DisplayName);

/// <summary>The clients of a tenant that a provisioned user reads.</summary>
/// <param name="ClientIds">The clients; an empty list keeps the membership without any client.</param>
public sealed record ProvisionedMembershipRequest([property: JsonRequired] IReadOnlyList<Guid> ClientIds);
