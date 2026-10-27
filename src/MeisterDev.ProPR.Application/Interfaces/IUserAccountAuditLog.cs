// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Emits the structured audit trail of a user account: the administrative changes to its lifecycle, and the
///     sign-ins and rights changes through the identity provider for platform administrators. Event names (for
///     example <c>user.disabled</c>, <c>user.reenabled</c>) are a contract that downstream log sinks route on, so
///     they are produced in exactly one place.
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
}
