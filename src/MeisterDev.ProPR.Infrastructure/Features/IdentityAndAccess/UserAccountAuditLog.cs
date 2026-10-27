// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;

/// <summary>
///     Serilog-backed implementation of <see cref="IUserAccountAuditLog" />. Each event carries a
///     stable <c>AuditEvent</c> property so downstream sinks can route on it, plus the target user
///     identifier, and the acting user for an administrator's change.
/// </summary>
public sealed class UserAccountAuditLog(ILogger<UserAccountAuditLog> logger) : IUserAccountAuditLog
{
    /// <summary>The value of the <c>Method</c> property of a sign-in through the provider for platform administrators.</summary>
    private const string PlatformIdentityProviderMethod = "platform_identity_provider";

    /// <summary>The value of the <c>Method</c> property of a sign-in through the provider for provisioned users.</summary>
    private const string ProvisionedIdentityProviderMethod = "provisioned_identity_provider";

    public void Disabled(Guid actorUserId, Guid targetUserId, string targetUsername)
    {
        logger.LogInformation(
            "{AuditEvent} actor={ActorUserId} target={TargetUserId} username={TargetUsername}",
            "user.disabled",
            actorUserId,
            targetUserId,
            targetUsername);
    }

    public void Reenabled(Guid actorUserId, Guid targetUserId, string targetUsername)
    {
        logger.LogInformation(
            "{AuditEvent} actor={ActorUserId} target={TargetUserId} username={TargetUsername}",
            "user.reenabled",
            actorUserId,
            targetUserId,
            targetUsername);
    }

    public void DisableBlockedByLastAdmin(Guid actorUserId, Guid targetUserId, string targetUsername)
    {
        logger.LogWarning(
            "{AuditEvent} blocked actor={ActorUserId} target={TargetUserId} username={TargetUsername} reason={Reason}",
            "user.disabled",
            actorUserId,
            targetUserId,
            targetUsername,
            "last_active_admin");
    }

    public void Deleted(Guid actorUserId, Guid targetUserId, string targetUsername)
    {
        logger.LogInformation(
            "{AuditEvent} actor={ActorUserId} target={TargetUserId} username={TargetUsername}",
            "user.deleted",
            actorUserId,
            targetUserId,
            targetUsername);
    }

    public void DeleteBlockedByLastAdmin(Guid actorUserId, Guid targetUserId, string targetUsername)
    {
        logger.LogWarning(
            "{AuditEvent} blocked actor={ActorUserId} target={TargetUserId} username={TargetUsername} reason={Reason}",
            "user.deleted",
            actorUserId,
            targetUserId,
            targetUsername,
            "last_active_admin");
    }

    public void SignedInThroughPlatformIdentityProvider(Guid userId, string username, string issuer, string subject)
    {
        logger.LogInformation(
            "{AuditEvent} target={TargetUserId} username={TargetUsername} method={Method} issuer={Issuer} subject={Subject}",
            "user.signed_in",
            userId,
            username,
            PlatformIdentityProviderMethod,
            issuer,
            subject);
    }

    public void PlatformIdentityProviderSignInRefused(Guid? userId, string? username, string issuer, string subject, string reason)
    {
        logger.LogWarning(
            "{AuditEvent} blocked target={TargetUserId} username={TargetUsername} method={Method} issuer={Issuer} subject={Subject} reason={Reason}",
            "user.signed_in",
            userId,
            username,
            PlatformIdentityProviderMethod,
            issuer,
            subject,
            reason);
    }

    public void PlatformAdministratorRightsGranted(Guid userId, string username, string issuer, string subject)
    {
        logger.LogInformation(
            "{AuditEvent} target={TargetUserId} username={TargetUsername} issuer={Issuer} subject={Subject}",
            "user.platform_admin_granted",
            userId,
            username,
            issuer,
            subject);
    }

    public void PlatformAdministratorRightsRevoked(Guid userId, string username, string issuer, string subject)
    {
        logger.LogInformation(
            "{AuditEvent} target={TargetUserId} username={TargetUsername} issuer={Issuer} subject={Subject}",
            "user.platform_admin_revoked",
            userId,
            username,
            issuer,
            subject);
    }

    public void SignedInThroughProvisionedIdentityProvider(Guid userId, string username, string issuer, string subject)
    {
        logger.LogInformation(
            "{AuditEvent} target={TargetUserId} username={TargetUsername} method={Method} issuer={Issuer} subject={Subject}",
            "user.signed_in",
            userId,
            username,
            ProvisionedIdentityProviderMethod,
            issuer,
            subject);
    }

    public void ProvisionedIdentityProviderSignInRefused(Guid? userId, string? username, string issuer, string subject, string reason)
    {
        logger.LogWarning(
            "{AuditEvent} blocked target={TargetUserId} username={TargetUsername} method={Method} issuer={Issuer} subject={Subject} reason={Reason}",
            "user.signed_in",
            userId,
            username,
            ProvisionedIdentityProviderMethod,
            issuer,
            subject,
            reason);
    }

    public void ProvisionedUserCreated(Guid actorUserId, Guid userId, string username, string issuer, string subject)
    {
        logger.LogInformation(
            "{AuditEvent} actor={ActorUserId} target={TargetUserId} username={TargetUsername} issuer={Issuer} subject={Subject}",
            "user.provisioned",
            actorUserId,
            userId,
            username,
            issuer,
            subject);
    }

    public void ProvisionedUserDisabledWithoutMembership(Guid actorUserId, Guid userId, string username)
    {
        logger.LogInformation(
            "{AuditEvent} actor={ActorUserId} target={TargetUserId} username={TargetUsername} reason={Reason}",
            "user.disabled",
            actorUserId,
            userId,
            username,
            "no_membership");
    }

    public void ProvisionedUserEnabledByMembership(Guid actorUserId, Guid userId, string username)
    {
        logger.LogInformation(
            "{AuditEvent} actor={ActorUserId} target={TargetUserId} username={TargetUsername} reason={Reason}",
            "user.reenabled",
            actorUserId,
            userId,
            username,
            "membership");
    }
}
