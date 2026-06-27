// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Features.IdentityAndAccess;

/// <summary>
///     Serilog-backed implementation of <see cref="IUserAccountAuditLog" />. Each event carries a
///     stable <c>AuditEvent</c> property so downstream sinks can route on it, plus the acting and
///     target user identifiers.
/// </summary>
public sealed class UserAccountAuditLog(ILogger<UserAccountAuditLog> logger) : IUserAccountAuditLog
{
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
}
