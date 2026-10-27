// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     The provisioning of the users of a hosting platform. The platform creates the user of a person who has an account
///     at the identity provider for provisioned users, and it sets the memberships of that user in the tenants of the
///     installation. ProPR knows the user by the issuer of the provider and the subject of the person at the provider.
/// </summary>
/// <remarks>
///     Every operation is idempotent. A user is active while it holds at least one membership: ProPR creates the user
///     inactive, enables it with its first membership, and disables it when the platform removes the last one. ProPR
///     then also revokes its refresh tokens and personal access tokens.
/// </remarks>
public interface IProvisionedUserService
{
    /// <summary>Creates the user of <paramref name="subject" />, or updates its email address and display name.</summary>
    /// <param name="subject">The subject of the person at the identity provider for provisioned users.</param>
    /// <param name="email">The email address of the person.</param>
    /// <param name="displayName">The display name of the person, or <see langword="null" />.</param>
    /// <param name="actorUserId">The platform administrator who calls the operation, when the caller is an account.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<ProvisioningResult<ProvisionedUserDto>> PutUserAsync(
        string subject,
        string email,
        string? displayName,
        Guid? actorUserId,
        CancellationToken ct = default);

    /// <summary>
    ///     Sets the membership of the user of <paramref name="subject" /> in the tenant: the tenant role tenant user and
    ///     the client-user role on exactly the clients of <paramref name="clientIds" />. A role on another client of the
    ///     tenant is removed.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="subject">The subject of the person at the identity provider for provisioned users.</param>
    /// <param name="clientIds">The clients of the tenant that the user reads.</param>
    /// <param name="actorUserId">The platform administrator who calls the operation, when the caller is an account.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<ProvisioningResult<ProvisionedMemberDto>> PutMembershipAsync(
        Guid tenantId,
        string subject,
        IReadOnlyCollection<Guid> clientIds,
        Guid? actorUserId,
        CancellationToken ct = default);

    /// <summary>
    ///     Removes the membership of the user of <paramref name="subject" /> in the tenant and its roles on the clients
    ///     of the tenant. A user without any membership is disabled, and its refresh tokens and personal access tokens
    ///     are revoked.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="subject">The subject of the person at the identity provider for provisioned users.</param>
    /// <param name="actorUserId">The platform administrator who calls the operation, when the caller is an account.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<ProvisioningResult<bool>> RemoveMembershipAsync(
        Guid tenantId,
        string subject,
        Guid? actorUserId,
        CancellationToken ct = default);

    /// <summary>Lists the provisioned users that hold a membership in the tenant, with their clients of the tenant.</summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    Task<ProvisioningResult<IReadOnlyList<ProvisionedMemberDto>>> ListMembersAsync(Guid tenantId, CancellationToken ct = default);
}

/// <summary>The outcome of a provisioning operation.</summary>
public enum ProvisioningOutcome
{
    /// <summary>The operation applied its change, or found the state it sets in place already.</summary>
    Succeeded = 0,

    /// <summary>The operation created the user.</summary>
    Created = 1,

    /// <summary>The installation configured no identity provider for provisioned users.</summary>
    ProviderNotConfigured = 2,

    /// <summary>The request carries a value that ProPR does not accept; the message names it.</summary>
    InvalidRequest = 3,

    /// <summary>The tenant does not exist.</summary>
    TenantNotFound = 4,

    /// <summary>The tenant is the System tenant, which has no provisioned members.</summary>
    SystemTenant = 5,

    /// <summary>No user is provisioned for the subject.</summary>
    UserNotFound = 6,

    /// <summary>A client of the request does not belong to the tenant.</summary>
    ClientNotInTenant = 7,

    /// <summary>The account of the subject holds platform administrator rights, which provisioning never changes.</summary>
    PlatformAdministrator = 8,
}

/// <summary>The outcome of a provisioning operation with its value.</summary>
/// <param name="Outcome">The outcome.</param>
/// <param name="Value">The value of a successful operation.</param>
/// <param name="Message">What the caller reads about a refused operation.</param>
/// <param name="UnknownClientIds">The clients of the request that do not belong to the tenant.</param>
public sealed record ProvisioningResult<T>(
    ProvisioningOutcome Outcome,
    T? Value = default,
    string? Message = null,
    IReadOnlyList<Guid>? UnknownClientIds = null)
{
    /// <summary>Whether the operation succeeded.</summary>
    public bool IsSuccess => this.Outcome is ProvisioningOutcome.Succeeded or ProvisioningOutcome.Created;
}

/// <summary>A user that a hosting platform provisioned.</summary>
/// <param name="Subject">The subject of the person at the identity provider for provisioned users.</param>
/// <param name="UserId">The account of the user.</param>
/// <param name="Username">The username of the account.</param>
/// <param name="Email">The email address that the platform sent.</param>
/// <param name="DisplayName">The display name that the platform sent.</param>
/// <param name="IsActive">Whether the account can sign in and use its personal access tokens.</param>
public sealed record ProvisionedUserDto(
    string Subject,
    Guid UserId,
    string Username,
    string Email,
    string? DisplayName,
    bool IsActive);

/// <summary>The membership of a provisioned user in a tenant.</summary>
/// <param name="Subject">The subject of the person at the identity provider for provisioned users.</param>
/// <param name="UserId">The account of the user.</param>
/// <param name="Email">The email address that the platform sent.</param>
/// <param name="DisplayName">The display name that the platform sent.</param>
/// <param name="IsActive">Whether the account can sign in and use its personal access tokens.</param>
/// <param name="Role">The role of the user in the tenant.</param>
/// <param name="ClientIds">The clients of the tenant on which the user holds a role.</param>
public sealed record ProvisionedMemberDto(
    string Subject,
    Guid UserId,
    string Email,
    string? DisplayName,
    bool IsActive,
    TenantRole Role,
    IReadOnlyList<Guid> ClientIds);
