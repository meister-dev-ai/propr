// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Emits the structured audit trail of a user account: the administrative changes to its lifecycle, the sign-ins
///     and rights changes through the identity provider for platform administrators, and the provisioning by a hosting
///     platform. Event names (for example <c>user.disabled</c>, <c>user.reenabled</c>) are a contract that downstream
///     log sinks route on, so they are produced in exactly one place.
/// </summary>
public interface IUserAccountAuditLog
{
    /// <summary>Records that an active user was disabled by an administrator.</summary>
    void Disabled(Guid actorUserId, Guid targetUserId, string targetUsername);

    /// <summary>Records that a disabled user was re-enabled by an administrator.</summary>
    void Reenabled(Guid actorUserId, Guid targetUserId, string targetUsername);

    /// <summary>
    ///     Records that a disable request was refused because it would have left the system with no
    ///     active global administrator. No state change occurred.
    /// </summary>
    void DisableBlockedByLastAdmin(Guid actorUserId, Guid targetUserId, string targetUsername);

    /// <summary>Records that a user was permanently deleted by an administrator.</summary>
    void Deleted(Guid actorUserId, Guid targetUserId, string targetUsername);

    /// <summary>
    ///     Records that a delete request was refused because it would have left the system with no
    ///     active global administrator. No deletion occurred.
    /// </summary>
    void DeleteBlockedByLastAdmin(Guid actorUserId, Guid targetUserId, string targetUsername);

    /// <summary>
    ///     Records that a user signed in through the identity provider for platform administrators. The issuer and
    ///     the subject name the account at the identity provider.
    /// </summary>
    void SignedInThroughPlatformIdentityProvider(Guid userId, string username, string issuer, string subject);

    /// <summary>
    ///     Records that the identity provider for platform administrators refused a sign-in. No session was issued.
    ///     The user is <see langword="null" /> when no account is linked to the identity at the provider.
    /// </summary>
    void PlatformIdentityProviderSignInRefused(Guid? userId, string? username, string issuer, string subject, string reason);

    /// <summary>
    ///     Records that a sign-in with the required role gave the account platform administrator rights, either as a
    ///     new account or as an account that had lost them.
    /// </summary>
    void PlatformAdministratorRightsGranted(Guid userId, string username, string issuer, string subject);

    /// <summary>Records that a sign-in without the required role removed the platform administrator rights of the account.</summary>
    void PlatformAdministratorRightsRevoked(Guid userId, string username, string issuer, string subject);

    /// <summary>
    ///     Records that a provisioned user signed in through the identity provider for provisioned users. The issuer and
    ///     the subject name the person at the provider.
    /// </summary>
    void SignedInThroughProvisionedIdentityProvider(Guid userId, string username, string issuer, string subject);

    /// <summary>
    ///     Records that the sign-in through the identity provider for provisioned users was refused. No session was
    ///     issued. The user is <see langword="null" /> when no user is provisioned for the identity.
    /// </summary>
    void ProvisionedIdentityProviderSignInRefused(Guid? userId, string? username, string issuer, string subject, string reason);

    /// <summary>
    ///     Records that a hosting platform provisioned the account of a person at the identity provider for provisioned
    ///     users. The actor and internal account identifiers correlate the event without including the username or subject.
    /// </summary>
    void ProvisionedUserCreated(Guid actorUserId, Guid userId, string issuer);

    /// <summary>Records that ProPR disabled a provisioned account because the hosting platform removed its last membership.</summary>
    void ProvisionedUserDisabledWithoutMembership(Guid actorUserId, Guid userId, string username);

    /// <summary>Records that ProPR enabled a provisioned account because the hosting platform set a membership.</summary>
    void ProvisionedUserEnabledByMembership(Guid actorUserId, Guid userId, string username);
}
