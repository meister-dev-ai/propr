// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.Ai.Providers.Hosting;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>
///     Short-lived named values one provider add-in keeps between the start of a flow and its completion.
/// </summary>
/// <remarks>
///     <para>
///         Addressed by a key the add-in chose rather than by a connection, because the callback that reads an
///         entry back carries an opaque state value and nothing else: the lookup is from that value to the
///         connection, before the connection is known.
///     </para>
///     <para>
///         Entries are scoped to the add-in that wrote them, so two families using one entry key never see each
///         other's, and a family cannot read the handshake of a family it shares a host with.
///     </para>
/// </remarks>
/// <param name="binding">The connection and family this store was handed to.</param>
/// <param name="actingPrincipalId">
///     The administrator the host recorded for the work this store was handed to, written onto every entry and
///     checked when one is claimed. Null outside an action invocation, which leaves entries unbound.
/// </param>
/// <param name="contextFactory">Opens a context per call, independent of whatever built this.</param>
/// <param name="secretProtectionCodec">Wraps and unwraps the stored values. A family never sees either form.</param>
/// <param name="timeProvider">Supplies the instant expiries are judged against.</param>
public sealed class ProviderKeyedStore(
    ProviderAddInBinding binding,
    Guid? actingPrincipalId,
    IDbContextFactory<MeisterProPRDbContext> contextFactory,
    ISecretProtectionCodec secretProtectionCodec,
    TimeProvider timeProvider) : IProviderKeyedStore
{
    /// <summary>The purpose the stored values are protected under.</summary>
    internal const string SecretPurpose = "AiProviderKeyedEntry";

    /// <inheritdoc />
    public async Task WriteAsync(
        string entryKey,
        IReadOnlyDictionary<string, string> values,
        DateTimeOffset expiresAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(values);

        var key = AcceptedKey(entryKey);
        AcceptExpiry(expiresAt, timeProvider.GetUtcNow());
        AcceptValues(values);

        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // The sweep, the count, the cap and the write are one transaction with the family's writes serialized
        // behind an advisory lock. Counted and then inserted without it, every concurrent writer sees the same
        // count and they all insert, so the cap holds only when nobody is writing at once; and two writers for
        // one new key both find no row and the second fails on the unique index instead of replacing what the
        // first wrote. The lock is per family, which is the scope the cap is stated in, so one family's writes
        // do not wait on another's.
        await ProviderAdvisoryLock.TakeAsync(db, ProviderAdvisoryLock.KeyedEntries, binding.AddInKey, ct)
            .ConfigureAwait(false);

        // Read after the lock, as the lease store reads it. Taken before it, the wait for the context and for the
        // lock was spent against a timestamp that had already gone stale, so an entry that expired during the
        // wait was still counted as live and the row was stamped with an instant that had passed.
        var now = timeProvider.GetUtcNow();

        // Every write takes the family's expired and consumed rows with it. A sweep on the write path rather
        // than a loop of its own, because an add-in that writes nothing accumulates nothing, and the work is
        // bounded by what that one family left behind.
        await SweepAsync(db, binding.AddInKey, now, ct).ConfigureAwait(false);

        var live = await db.ProviderKeyedEntries
            .CountAsync(
                entry => entry.AddInKey == binding.AddInKey
                         && entry.ConsumedAt == null
                         && entry.ExpiresAt > now
                         && entry.EntryKey != key,
                ct)
            .ConfigureAwait(false);

        if (live >= ProviderHostLimits.MaximumLiveEntriesPerAddIn)
        {
            throw new ProviderRequestRejectedException(
                $"The provider family '{binding.AddInKey}' already holds {live} unclaimed entries, which is the "
                + "most the host keeps for one family at a time. Entries are claimed once and expire; one that "
                + "is neither means a flow was started and never finished.",
                nameof(entryKey));
        }

        var protectedValue = secretProtectionCodec.Protect(
            JsonSerializer.Serialize(values.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal)),
            SecretPurpose);

        var existing = await db.ProviderKeyedEntries
            .FirstOrDefaultAsync(entry => entry.AddInKey == binding.AddInKey && entry.EntryKey == key, ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            db.ProviderKeyedEntries.Add(
                new ProviderKeyedEntryRecord
                {
                    Id = Guid.NewGuid(),
                    AddInKey = binding.AddInKey,
                    EntryKey = key,
                    ProtectedValue = protectedValue,
                    CreatedAt = now,
                    ExpiresAt = expiresAt,
                    ActingPrincipalId = actingPrincipalId,
                });
        }
        else
        {
            // Rewritten rather than refused: a flow restarted under the same state value is the same flow, and
            // the previous attempt's expiry and consumed mark have nothing to say about this one.
            existing.ProtectedValue = protectedValue;
            existing.CreatedAt = now;
            existing.ExpiresAt = expiresAt;
            existing.ConsumedAt = null;
            existing.ActingPrincipalId = actingPrincipalId;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ProviderClaimOutcome> ClaimAsync(string entryKey, CancellationToken ct = default)
    {
        var key = AcceptedKey(entryKey);

        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // The claim and the read of what was claimed share a transaction, so the winner reads the row it just
        // took. Without it a sweep running between the two would remove the row as consumed and the winner would
        // come away with nothing.
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Read where it is used. Taken before the context was opened, it was the instant the call was made, and
        // an entry that expired while the context was being opened was still claimable against it.
        var now = timeProvider.GetUtcNow();

        // One conditional statement, so single use holds. A read followed by a write would let two callers both
        // find the entry unconsumed and both be told they won, which a browser delivering one callback
        // twice produces. The acting principal is the fourth clause of the same predicate, so a completion that
        // carries no session still cannot be claimed by an administrator other than the one who started it.
        var claimed = await db.ProviderKeyedEntries
            .Where(entry => entry.AddInKey == binding.AddInKey
                            && entry.EntryKey == key
                            && entry.ConsumedAt == null
                            && entry.ExpiresAt > now
                            && (entry.ActingPrincipalId == null || entry.ActingPrincipalId == actingPrincipalId))
            .ExecuteUpdateAsync(update => update.SetProperty(entry => entry.ConsumedAt, now), ct)
            .ConfigureAwait(false);

        if (claimed == 0)
        {
            var refusal = await this.DiagnoseAsync(db, key, now, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return ProviderClaimOutcome.Refused(refusal);
        }

        var stored = await db.ProviderKeyedEntries
            .AsNoTracking()
            .Where(entry => entry.AddInKey == binding.AddInKey && entry.EntryKey == key)
            .Select(entry => entry.ProtectedValue)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return new ProviderClaimOutcome(ProviderClaimRefusal.None, this.Decode(stored));
    }

    /// <summary>Removes the entries of one family that are past their expiry or already claimed.</summary>
    /// <param name="db">The context to sweep in.</param>
    /// <param name="addInKey">The family whose entries are swept.</param>
    /// <param name="now">The instant expiries are judged against.</param>
    /// <param name="ct">Cancels the sweep.</param>
    internal static Task<int> SweepAsync(
        MeisterProPRDbContext db,
        string addInKey,
        DateTimeOffset now,
        CancellationToken ct)
    {
        return db.ProviderKeyedEntries
            .Where(entry => entry.AddInKey == addInKey && (entry.ExpiresAt <= now || entry.ConsumedAt != null))
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>
    ///     Why a claim that matched nothing did not match, read after the fact.
    /// </summary>
    /// <remarks>
    ///     Each of the four is a different thing for an operator to do, so they stay apart rather than collapsing
    ///     into one refusal. Read separately from the claim on purpose: the claim has to be one statement to be
    ///     single-use, and one statement can report that it matched nothing but not which clause failed.
    /// </remarks>
    /// <param name="db">The context the claim ran in.</param>
    /// <param name="key">The key the claim was for.</param>
    /// <param name="now">The instant expiries are judged against.</param>
    /// <param name="ct">Cancels the read.</param>
    private async Task<ProviderClaimRefusal> DiagnoseAsync(
        MeisterProPRDbContext db,
        string key,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var entry = await db.ProviderKeyedEntries
            .AsNoTracking()
            .Where(candidate => candidate.AddInKey == binding.AddInKey && candidate.EntryKey == key)
            .Select(candidate => new { candidate.ConsumedAt, candidate.ExpiresAt, candidate.ActingPrincipalId })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (entry is null)
        {
            return ProviderClaimRefusal.NoSuchEntry;
        }

        // Consumed first: an entry that was claimed and has since passed its expiry is a callback delivered
        // twice, which is the ordinary case, and reporting it as expired would send an operator looking for a
        // flow that took too long.
        if (entry.ConsumedAt is not null)
        {
            return ProviderClaimRefusal.AlreadyConsumed;
        }

        if (entry.ExpiresAt <= now)
        {
            return ProviderClaimRefusal.Expired;
        }

        return entry.ActingPrincipalId != actingPrincipalId
            ? ProviderClaimRefusal.WrongPrincipal
            : ProviderClaimRefusal.NoSuchEntry;
    }

    private static string AcceptedKey(string entryKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryKey);

        return entryKey.Length <= ProviderHostLimits.MaximumEntryKeyLength
            ? entryKey
            : throw new ProviderRequestRejectedException(
                $"The entry key is {entryKey.Length} characters; the host addresses entries by at most "
                + $"{ProviderHostLimits.MaximumEntryKeyLength}.",
                nameof(entryKey));
    }

    private static void AcceptExpiry(DateTimeOffset expiresAt, DateTimeOffset now)
    {
        if (expiresAt <= now)
        {
            throw new ProviderRequestRejectedException(
                $"The entry's 'expiresAt' is {expiresAt:O}, which is not in the future. Every entry expires, and "
                + "one written already expired could never be claimed.",
                nameof(expiresAt));
        }

        if (expiresAt - now > ProviderHostLimits.MaximumEntryLifetime)
        {
            throw new ProviderRequestRejectedException(
                $"The entry is asked to live longer than {ProviderHostLimits.MaximumEntryLifetime.TotalMinutes:0} "
                + "minutes. An entry holds what a flow needs between its start and its completion, and a flow an "
                + "operator completes in a browser does not take that long.",
                nameof(expiresAt));
        }
    }

    private static void AcceptValues(IReadOnlyDictionary<string, string> values)
    {
        if (values.Count > ProviderHostLimits.MaximumEntryValueCount)
        {
            throw new ProviderRequestRejectedException(
                $"The entry carries {values.Count} values; the host stores at most "
                + $"{ProviderHostLimits.MaximumEntryValueCount}.",
                nameof(values));
        }

        foreach (var (name, value) in values)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ProviderRequestRejectedException(
                    "An entry value has no name. The family reads its values back by name, so an unnamed one "
                    + "cannot be read at all.",
                    nameof(values));
            }

            if (name.Length > ProviderHostLimits.MaximumValueNameLength)
            {
                throw new ProviderRequestRejectedException(
                    $"An entry value's name is {name.Length} characters; the host stores names of at most "
                    + $"{ProviderHostLimits.MaximumValueNameLength}.",
                    nameof(values));
            }

            // The dictionary says its values are not null, and an add-in is compiled separately so nothing
            // enforces that at this boundary. A null slipped past the length check below and was persisted,
            // surfacing later wherever the value was read as a string.
            if (value is null)
            {
                throw new ProviderRequestRejectedException(
                    $"The entry value '{name}' is null. The host stores values, and an absent one is an absent "
                    + "entry rather than a stored nothing.",
                    nameof(values));
            }

            if (value.Length > ProviderHostLimits.MaximumEntryValueLength)
            {
                throw new ProviderRequestRejectedException(
                    $"The entry value '{name}' is {value.Length} characters; the host stores at most "
                    + $"{ProviderHostLimits.MaximumEntryValueLength}.",
                    nameof(values));
            }
        }
    }

    private IReadOnlyDictionary<string, string> Decode(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(secretProtectionCodec.Unprotect(stored, SecretPurpose));

        return values is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(values, StringComparer.Ordinal);
    }
}
