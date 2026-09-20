// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.UsageReporting.Persistence;

/// <summary>
///     The provider identity a daily usage sample is attributed to: the identity the connection profile stores,
///     read from the row itself.
/// </summary>
/// <remarks>
///     <para>
///         <c>client_token_usage_samples.provider_kind</c> is part of the unique index the daily sample is
///         accumulated through, so the value two callers write for the same connection decides whether one series
///         accumulates or two do. Reading it from the connection row gives every caller the same answer, and ties
///         it to the identity the connection carries: the migration that rewrites a family's connection rows
///         rewrites its usage rows in the same statement, and nothing between the two moments writes a spelling
///         the other producer does not.
///     </para>
///     <para>
///         The empty string stands for a sample that cannot be attributed — no connection, or one deleted since
///         the tokens were spent. An unattributed row is still worth keeping, because refusing to record usage
///         over a missing profile would lose the spend entirely.
///     </para>
/// </remarks>
internal static class UsageSampleProviderIdentity
{
    /// <summary>Reads the identity stored against <paramref name="connectionId" />.</summary>
    /// <param name="db">The context to read through.</param>
    /// <param name="connectionId">The connection the tokens were spent on, if one is known.</param>
    /// <param name="ct">Cancels the read.</param>
    internal static async Task<string> ReadAsync(
        MeisterProPRDbContext db,
        Guid? connectionId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (connectionId is null || connectionId == Guid.Empty)
        {
            return string.Empty;
        }

        var storedIdentity = await db.AiConnectionProfiles
            .AsNoTracking()
            .Where(profile => profile.Id == connectionId)
            .Select(profile => profile.ProviderKind)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return storedIdentity ?? string.Empty;
    }
}
