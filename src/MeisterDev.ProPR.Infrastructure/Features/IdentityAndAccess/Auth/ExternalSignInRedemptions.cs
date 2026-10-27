// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Configurations;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MeisterDev.ProPR.Infrastructure.Auth;

/// <summary>
///     Accepts each one-time value of an external sign-in once: the state of a sign-in at its callback, and a pending
///     link when it is confirmed or turned into a new account. The values travel in cookies and addresses that a
///     browser can send again, so ProPR records every value it accepts and refuses a recorded one. A record stays until
///     its value expires, because ProPR refuses an expired value for its age.
/// </summary>
/// <remarks>
///     The record holds the SHA-256 hash of the value, not the value. The caller saves nothing else in the same context
///     while it redeems a value, because the redemption saves the context.
/// </remarks>
public sealed class ExternalSignInRedemptions(MeisterProPRDbContext dbContext)
{
    /// <summary>Records <paramref name="oneTimeValue" /> as used, unless it was used before.</summary>
    /// <param name="oneTimeValue">The state of a sign-in, or the identifier of a pending link.</param>
    /// <param name="expiresAt">When the value expires; the record is kept until then.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true" /> for the first use of the value, <see langword="false" /> for any later one.</returns>
    public async Task<bool> TryRedeemAsync(string oneTimeValue, DateTimeOffset expiresAt, CancellationToken ct)
    {
        var key = KeyOf(oneTimeValue);
        await this.RemoveExpiredAsync(DateTimeOffset.UtcNow, ct);

        if (await dbContext.ExternalSignInRedemptions.AnyAsync(redemption => redemption.RedemptionKey == key, ct))
        {
            await dbContext.SaveChangesAsync(ct);
            return false;
        }

        var record = new ExternalSignInRedemptionRecord { RedemptionKey = key, ExpiresAt = expiresAt };
        dbContext.ExternalSignInRedemptions.Add(record);
        try
        {
            await dbContext.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException exception) when (IsSecondUse(exception))
        {
            // Two requests with the same value at the same time both found no record. The primary key refuses the
            // second one, which leaves its record behind in this context; it is detached so a later save does not
            // repeat it.
            dbContext.Entry(record).State = EntityState.Detached;
            return false;
        }
    }

    /// <summary>Whether <paramref name="oneTimeValue" /> was used already.</summary>
    /// <param name="oneTimeValue">The state of a sign-in, or the identifier of a pending link.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<bool> IsRedeemedAsync(string oneTimeValue, CancellationToken ct)
    {
        var key = KeyOf(oneTimeValue);
        return dbContext.ExternalSignInRedemptions.AnyAsync(redemption => redemption.RedemptionKey == key, ct);
    }

    /// <summary>
    ///     Removes the records of expired values with one statement. When two requests remove the same records at the
    ///     same time, the statement of the second finds them gone and removes nothing. Records loaded and removed through
    ///     the change tracker would instead fail the save of the second request, and with it the redemption of its own
    ///     value.
    /// </summary>
    private async Task RemoveExpiredAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (dbContext.Database.IsRelational())
        {
            await dbContext.ExternalSignInRedemptions.Where(redemption => redemption.ExpiresAt <= now).ExecuteDeleteAsync(ct);
            return;
        }

        // A store without set-based statements, such as the in-memory store, removes the loaded records with the next
        // save.
        dbContext.ExternalSignInRedemptions.RemoveRange(
            await dbContext.ExternalSignInRedemptions.Where(redemption => redemption.ExpiresAt <= now).ToListAsync(ct));
    }

    private static string KeyOf(string oneTimeValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oneTimeValue);
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(oneTimeValue)));
    }

    private static bool IsSecondUse(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: ExternalSignInRedemptionEntityTypeConfiguration.PrimaryKeyName,
        };
    }
}
