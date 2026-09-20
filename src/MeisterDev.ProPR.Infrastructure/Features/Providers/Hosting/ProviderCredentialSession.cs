// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Data.Common;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Hosting;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>
///     One connection's credential row, held from the moment it is taken until the session is disposed.
/// </summary>
/// <remarks>
///     <para>
///         The row lock, the read, whatever the family does with what it read, and the write are all inside one
///         transaction. Three independent calls could not express that, and the property it buys is the one the
///         whole primitive exists for: exactly one exchange per expiry, with the callers that lost reading back
///         the winner's credential.
///     </para>
///     <para>
///         A session that is disposed without being completed rolls back, which leaves the credential as it was
///         rather than half written.
///     </para>
/// </remarks>
public sealed class ProviderCredentialSession : IProviderCredentialSession
{
    private readonly ProviderAddInBinding _binding;
    private readonly MeisterProPRDbContext _db;
    private readonly ProviderInvocationCredentialGrant? _grant;
    private readonly ISecretProtectionCodec _secretProtectionCodec;
    private readonly TimeProvider _timeProvider;
    private readonly IDbContextTransaction _transaction;

    private bool _completed;
    private bool _disposed;

    /// <summary>Takes ownership of the context and the transaction the row was locked in.</summary>
    /// <param name="binding">The connection this session acts on and the family serving it.</param>
    /// <param name="db">The context the lock was taken on, disposed with the session.</param>
    /// <param name="transaction">The transaction holding the lock, disposed with the session.</param>
    /// <param name="secretProtectionCodec">Wraps and unwraps the stored credential.</param>
    /// <param name="timeProvider">Supplies the instant an expiry is judged against.</param>
    /// <param name="grant">
    ///     Present while an operator-started action is in flight: what is re-checked before the credential is
    ///     written, and the administrator recorded as its owner.
    /// </param>
    internal ProviderCredentialSession(
        ProviderAddInBinding binding,
        MeisterProPRDbContext db,
        IDbContextTransaction transaction,
        ISecretProtectionCodec secretProtectionCodec,
        TimeProvider timeProvider,
        ProviderInvocationCredentialGrant? grant = null)
    {
        this._binding = binding;
        this._db = db;
        this._transaction = transaction;
        this._secretProtectionCodec = secretProtectionCodec;
        this._timeProvider = timeProvider;
        this._grant = grant;
    }

    /// <inheritdoc />
    public async Task<ProviderStoredCredential> ReadAsync(CancellationToken ct = default)
    {
        this.RefuseWhenFinished();

        var stored = await this._db.AiConnectionProfiles
            .AsNoTracking()
            .Where(profile => profile.Id == this._binding.ConnectionProfileId)
            .Select(profile => profile.ProtectedSecret)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return ProviderCredentialSessions.Decode(
            stored,
            this._binding.AuthMode,
            this._secretProtectionCodec,
            this._binding.AddInKey);
    }

    /// <inheritdoc />
    public async Task WriteAsync(
        IReadOnlyDictionary<string, string> fields,
        DateTimeOffset expiresAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fields);
        this.RefuseWhenFinished();

        // Before the write and not before the session: the flow between the two takes minutes, which is long
        // enough for the role that authorized it to be revoked and for the entitlement to lapse. Read on this
        // context, so the check and the write it authorizes are one transaction.
        if (this._grant is { } grant)
        {
            await grant.RefuseWhenWithdrawnAsync(this._db, ct).ConfigureAwait(false);
        }

        var accepted = this.Accepted(fields, expiresAt);

        var envelope = new ProviderSecretEnvelope(this._binding.AuthMode.ToString(), accepted)
        {
            ExpiresAt = expiresAt,

            // The family that ran the flow is recorded with the credential it produced, so a later read by
            // another family is a mismatch it can see rather than a payload it decodes against the wrong shape.
            IdentityKey = this._binding.AddInKey,
        };

        // Protected here rather than inside the update expression: the update is translated to SQL, and a call
        // the translator cannot read would either be evaluated somewhere unintended or refused outright.
        var protectedSecret = this._secretProtectionCodec.Protect(
            envelope.Encode(),
            ProviderCredentialSessions.SecretPurpose);
        var writtenAt = this._timeProvider.GetUtcNow();

        // The grant owner rides on the same statement, inside the same transaction as the credential it belongs
        // to, so a credential can never be stored without the record of who authorized it. The values come from
        // the host and the family sees none of them.
        var ownerAdminId = this._grant?.OwnerAdminId;
        var ownerDisplayName = this._grant?.OwnerDisplayName;

        var updated = this._grant is null
            // A write with no grant replaces the credential and clears the record of who authorized the one it
            // replaced. Left in place, a credential refreshed by the family kept the previous administrator's
            // name against it and read as though they had authorized this one.
            ? await this._db.AiConnectionProfiles
                .Where(profile => profile.Id == this._binding.ConnectionProfileId)
                .ExecuteUpdateAsync(
                    update => update
                        .SetProperty(profile => profile.ProtectedSecret, protectedSecret)
                        .SetProperty(profile => profile.UpdatedAt, writtenAt)
                        .SetProperty(profile => profile.CredentialOwnerAdminId, (Guid?)null)
                        .SetProperty(profile => profile.CredentialOwnerDisplayName, (string?)null)
                        .SetProperty(profile => profile.CredentialAuthorizedAt, (DateTimeOffset?)null),
                    ct)
                .ConfigureAwait(false)
            : await this._db.AiConnectionProfiles
                .Where(profile => profile.Id == this._binding.ConnectionProfileId)
                .ExecuteUpdateAsync(
                    update => update
                        .SetProperty(profile => profile.ProtectedSecret, protectedSecret)
                        .SetProperty(profile => profile.UpdatedAt, writtenAt)
                        .SetProperty(profile => profile.CredentialOwnerAdminId, ownerAdminId)
                        .SetProperty(profile => profile.CredentialOwnerDisplayName, ownerDisplayName)
                        .SetProperty(profile => profile.CredentialAuthorizedAt, writtenAt),
                    ct)
                .ConfigureAwait(false);

        if (updated == 0)
        {
            throw new ProviderRequestRejectedException(
                $"The connection '{this._binding.ConnectionDisplayName}' no longer exists, so there is nothing to "
                + "store the credential against.");
        }
    }

    /// <inheritdoc />
    public async Task ClearAsync(CancellationToken ct = default)
    {
        this.RefuseWhenFinished();

        // The same re-check a write makes, for the same reason: the flow that reached here takes minutes, which
        // is long enough for the role that authorized it to be revoked. Removing a credential is as much a change
        // to the connection as replacing one.
        if (this._grant is { } grant)
        {
            await grant.RefuseWhenWithdrawnAsync(this._db, ct).ConfigureAwait(false);
        }

        var clearedAt = this._timeProvider.GetUtcNow();

        // The health and the grant owner go in the same statement as the credential they describe. Left behind,
        // the health would report a state observed on a credential that no longer exists, and the owner would
        // name the administrator who authorized one that has been removed.
        var updated = await this._db.AiConnectionProfiles
            .Where(profile => profile.Id == this._binding.ConnectionProfileId)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(profile => profile.ProtectedSecret, (string?)null)
                    .SetProperty(profile => profile.CredentialHealth, (string?)null)
                    .SetProperty(profile => profile.CredentialHealthCause, (string?)null)
                    .SetProperty(profile => profile.CredentialHealthChangedAt, (DateTimeOffset?)null)
                    .SetProperty(profile => profile.CredentialOwnerAdminId, (Guid?)null)
                    .SetProperty(profile => profile.CredentialOwnerDisplayName, (string?)null)
                    .SetProperty(profile => profile.CredentialAuthorizedAt, (DateTimeOffset?)null)
                    .SetProperty(profile => profile.UpdatedAt, clearedAt),
                ct)
            .ConfigureAwait(false);

        if (updated == 0)
        {
            throw new ProviderRequestRejectedException(
                $"The connection '{this._binding.ConnectionDisplayName}' no longer exists, so there is no stored "
                + "credential to remove.");
        }
    }

    /// <inheritdoc />
    public async Task CompleteAsync(CancellationToken ct = default)
    {
        this.RefuseWhenFinished();

        await this._transaction.CommitAsync(ct).ConfigureAwait(false);
        this._completed = true;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (this._disposed)
        {
            return;
        }

        this._disposed = true;

        if (!this._completed)
        {
            // Best effort and not conditional on the caller's token: a session abandoned because its caller was
            // cancelled still has to give the row back, and a rollback on a transaction the server already ended
            // is not something to fail disposal over.
            try
            {
                await this._transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Every exception, not the two that were named. The rollback is best effort, and anything it
                // raises escaped disposal before the block below ran, leaking the transaction and the pooled
                // connection its context holds. _disposed is already set, so nothing retries this.
            }
        }

        // The context is disposed whatever the transaction's disposal does. Without this, a transaction disposal
        // that throws leaks the context and the pooled connection it holds, and a connection leaked per session
        // exhausts the pool.
        try
        {
            await this._transaction.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await this._db.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     The fields as they will be stored, or a refusal naming what was wrong with them.
    /// </summary>
    /// <remarks>
    ///     The expiry is checked here rather than when the credential is next read, because this is where the
    ///     message can still name the field the family failed to supply. A credential with no stated expiry is
    ///     treated as never usable everywhere it is read, so accepting one produces a connection that verifies,
    ///     appears configured, and then renews before every call — which serialises a whole review through this
    ///     one row lock, with nothing reporting a cause.
    /// </remarks>
    /// <param name="fields">The fields as the family supplied them.</param>
    /// <param name="expiresAt">The expiry as the family supplied it.</param>
    private IReadOnlyDictionary<string, string> Accepted(
        IReadOnlyDictionary<string, string> fields,
        DateTimeOffset expiresAt)
    {
        if (expiresAt == default)
        {
            throw new ProviderRequestRejectedException(
                "A credential stored through the host states when it stops being usable, and 'expiresAt' was not "
                + "supplied. A credential with no expiry is treated as never usable, so every call would renew it.",
                nameof(expiresAt));
        }

        var now = this._timeProvider.GetUtcNow();
        if (expiresAt <= now)
        {
            throw new ProviderRequestRejectedException(
                $"The credential's 'expiresAt' is {expiresAt:O}, which has already passed. A credential that is "
                + "already expired when it is stored leaves the connection unusable the moment it is written.",
                nameof(expiresAt));
        }

        // Counted over the fields that hold something, because a blank value is read back as absent everywhere a
        // credential is read. A map of blanks would store a connection that verifies, appears configured, and
        // presents nothing.
        if (!fields.Any(field => !string.IsNullOrWhiteSpace(field.Value)))
        {
            throw new ProviderRequestRejectedException(
                "A credential stored through the host carries at least one field with a value. Storing none, or "
                + "storing only blanks, would leave the connection with an expiry and nothing to present.",
                nameof(fields));
        }

        if (fields.Count > ProviderHostLimits.MaximumCredentialFieldCount)
        {
            throw new ProviderRequestRejectedException(
                $"The credential carries {fields.Count} fields; the host stores at most "
                + $"{ProviderHostLimits.MaximumCredentialFieldCount}.",
                nameof(fields));
        }

        foreach (var (name, value) in fields)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ProviderRequestRejectedException(
                    "A credential field has no name. The family reads its fields back by name, so an unnamed one "
                    + "cannot be read at all.",
                    nameof(fields));
            }

            if (name.Length > ProviderHostLimits.MaximumValueNameLength)
            {
                throw new ProviderRequestRejectedException(
                    $"A credential field's name is {name.Length} characters; the host stores names of at most "
                    + $"{ProviderHostLimits.MaximumValueNameLength}.",
                    nameof(fields));
            }

            if (value?.Length > ProviderHostLimits.MaximumCredentialFieldLength)
            {
                throw new ProviderRequestRejectedException(
                    $"The credential field '{name}' is {value.Length} characters; the host stores at most "
                    + $"{ProviderHostLimits.MaximumCredentialFieldLength}.",
                    nameof(fields));
            }
        }

        return fields.ToDictionary(field => field.Key, field => field.Value ?? string.Empty, StringComparer.Ordinal);
    }

    private void RefuseWhenFinished()
    {
        ObjectDisposedException.ThrowIf(this._disposed, this);

        if (this._completed)
        {
            throw new InvalidOperationException(
                "This credential session has been completed. The row it held is back with whoever waits for it "
                + "next, so nothing can still be read or written through it.");
        }
    }
}
