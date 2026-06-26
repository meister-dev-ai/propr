// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Emits the structured audit trail for administrative changes to a user account's
///     lifecycle. Event names (for example <c>user.disabled</c>, <c>user.reenabled</c>) are a
///     contract that downstream log sinks route on, so they are produced in exactly one place.
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
}
