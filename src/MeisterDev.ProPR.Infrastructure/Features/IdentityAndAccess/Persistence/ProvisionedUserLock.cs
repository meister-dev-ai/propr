// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Infrastructure.Data;

namespace MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess.Persistence;

/// <summary>
///     The lock that serializes the changes of the access of one provisioned user: the membership changes of the hosting
///     platform, and the decision of a platform administrator to enable or disable the account. A change takes the lock in
///     its transaction before it reads the memberships and the account, so a second change waits for the first one and
///     reads what the first one committed.
/// </summary>
internal static class ProvisionedUserLock
{
    /// <summary>Takes the lock of the user in PostgreSQL until the transaction of <paramref name="db" /> ends.</summary>
    /// <param name="db">The context whose transaction holds the lock.</param>
    /// <param name="userId">The account of the provisioned user.</param>
    /// <param name="ct">Cancellation token for the operation.</param>
    public static Task AcquireAsync(MeisterProPRDbContext db, Guid userId, CancellationToken ct)
    {
        return PostgresAdvisoryLocks.AcquireTransactionAsync(db, "provisioned-user:" + userId.ToString("N"), ct);
    }
}
