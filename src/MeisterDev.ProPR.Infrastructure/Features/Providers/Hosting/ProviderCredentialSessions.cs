// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Hosting;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>
///     Reaches the stored credential of the one connection this instance was built for, either to read it or to
///     take it exclusively for a renewal.
/// </summary>
/// <remarks>
///     <para>
///         Held by a provider family for as long as it holds the client the host built for it, which is longer
///         than the work that built it: a review builds one client and calls it for as long as the review runs,
///         and its later passes run after the credential the client started with has expired. Nothing here is
///         resolved from the work that built the client — each call opens a context of its own from the factory —
///         so it stays usable after that work is over and safe to call from every thread the client serves.
///     </para>
///     <para>
///         The database context is the reason the factory is taken rather than a context: a review's parallel
///         passes would otherwise drive one context at once, which Entity Framework refuses.
///     </para>
/// </remarks>
/// <param name="binding">The connection this instance acts on and the family serving it.</param>
/// <param name="contextFactory">Opens a context per call, independent of whatever built this.</param>
/// <param name="secretProtectionCodec">Wraps and unwraps the stored credential. A family never sees either form.</param>
/// <param name="capabilities">The licence check made before a credential is handed over.</param>
/// <param name="timeProvider">Supplies the instant an expiry is judged against.</param>
/// <param name="lockWait">
///     How long a caller waits for the credential row before giving up, or null for the host's own wait.
/// </param>
/// <param name="grant">
///     Present while an operator-started action is in flight. It re-checks the owner role and the declared
///     capability at the moment a credential is written, and it carries the administrator recorded as the grant's
///     owner. Absent on the review path, where nothing new is being authorized.
/// </param>
public sealed class ProviderCredentialSessions(
    ProviderAddInBinding binding,
    IDbContextFactory<MeisterProPRDbContext> contextFactory,
    ISecretProtectionCodec secretProtectionCodec,
    IProviderAddInCapabilityGate capabilities,
    TimeProvider timeProvider,
    TimeSpan? lockWait = null,
    ProviderInvocationCredentialGrant? grant = null) : IProviderCredentialSessions
{
    /// <summary>
    ///     The purpose the connection credential is protected under. Constant across connections, as it is on
    ///     every other path that reads one.
    /// </summary>
    internal const string SecretPurpose = "AiConnectionApiKey";


    /// <summary>
    ///     Bounds how long a session may hold the row without doing anything. The server ends a transaction that
    ///     sits idle past this, which releases the row lock; a family that takes the credential and never returns
    ///     would otherwise stop every other caller on that connection renewing for the life of the process.
    /// </summary>
    private static readonly string IdleTimeoutStatement =
        $"SET LOCAL idle_in_transaction_session_timeout = "
        + $"'{(int)ProviderHostLimits.CredentialSessionLifetime.TotalMilliseconds}ms'";

    /// <summary>How long this handle's callers wait for the credential row.</summary>
    private TimeSpan LockWait => lockWait ?? ProviderHostLimits.CredentialLockWait;

    /// <summary>
    ///     The wait as PostgreSQL reads it. A <c>lock_timeout</c> of zero means no timeout there, so a wait that
    ///     rounds down to nothing is raised to a millisecond: a caller asking not to wait must not be made to
    ///     wait for ever.
    /// </summary>
    private int LockWaitMilliseconds => Math.Max(1, (int)this.LockWait.TotalMilliseconds);

    /// <inheritdoc />
    public async Task<ProviderStoredCredential> ReadAsync(CancellationToken ct = default)
    {
        await this.RefuseUnlicensedAsync(ct).ConfigureAwait(false);

        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var stored = await db.AiConnectionProfiles
            .AsNoTracking()
            .Where(profile => profile.Id == binding.ConnectionProfileId)
            .Select(profile => new StoredRow(profile.ProviderKind, profile.ProtectedSecret))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        RefuseWhenRepointed(binding, stored?.ProviderIdentity);

        return Decode(stored?.ProtectedSecret, binding.AuthMode, secretProtectionCodec, binding.AddInKey);
    }

    /// <inheritdoc />
    public async Task<IProviderCredentialSession> OpenAsync(CancellationToken ct = default)
    {
        await this.RefuseUnlicensedAsync(ct).ConfigureAwait(false);

        var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        try
        {
            var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                // Bounds how long this caller waits for the row. Applied to the transaction only, so it
                // governs this wait and nothing else the connection does.
                await db.Database
                    .ExecuteSqlRawAsync(
                        $"SET LOCAL lock_timeout = '{this.LockWaitMilliseconds}ms'",
                        ct)
                    .ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync(IdleTimeoutStatement, ct).ConfigureAwait(false);

                // The lock, the read, the family's exchange with the vendor and the write all sit inside this
                // transaction. That is what makes exactly one exchange happen per expiry however many callers
                // arrive: the callers that lost read back the winner's credential rather than each rotating the
                // one they started with, which with a rotating credential loses it permanently.
                await db.Database.ExecuteSqlRawAsync(
                        "SELECT id FROM ai_connection_profiles WHERE id = {0} FOR UPDATE",
                        [binding.ConnectionProfileId],
                        ct)
                    .ConfigureAwait(false);

                // Read again with the row held. The wait for the lock is unbounded from this caller's point of
                // view — another caller's exchange with a vendor happens inside it — and an entitlement checked
                // before the wait says nothing about the moment the credential is handed over. The same wait is
                // long enough for the connection to be repointed to another family.
                await this.RefuseUnlicensedAsync(ct).ConfigureAwait(false);
                RefuseWhenRepointed(
                    binding,
                    await db.AiConnectionProfiles
                        .AsNoTracking()
                        .Where(profile => profile.Id == binding.ConnectionProfileId)
                        .Select(profile => profile.ProviderKind)
                        .FirstOrDefaultAsync(ct)
                        .ConfigureAwait(false));

                return new ProviderCredentialSession(
                    binding,
                    db,
                    transaction,
                    secretProtectionCodec,
                    timeProvider,
                    grant);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
                throw new ProviderHostTimeoutException(
                    $"The credential for connection '{binding.ConnectionDisplayName}' was held by another caller "
                    + $"for longer than {this.LockWait.TotalSeconds:0.###} seconds.",
                    exception);
            }
            catch
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            await db.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Reads a stored value into the shape a family sees.</summary>
    /// <param name="stored">The protected column value, or null when nothing is stored.</param>
    /// <param name="authMode">The authentication mode the connection was configured for.</param>
    /// <param name="secretProtectionCodec">Unwraps the stored value.</param>
    /// <param name="readingKey">The identity key of the family reading, or null where none is known.</param>
    /// <exception cref="ProviderRequestRejectedException">
    ///     The stored credential was written by a different family.
    /// </exception>
    internal static ProviderStoredCredential Decode(
        string? stored,
        string authMode,
        ISecretProtectionCodec secretProtectionCodec,
        string? readingKey = null)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return ProviderStoredCredential.None;
        }

        var envelope = ProviderSecretEnvelope.Decode(
            secretProtectionCodec.Unprotect(stored, SecretPurpose),
            authMode);

        // Refused rather than handed over. This path feeds a credential to the family about to present it, and a
        // field name means whatever the family that wrote it says it means: one family's service-account
        // document read as another's API key reaches the provider as a credential nobody configured.
        if (envelope.DescribeForeignRead(readingKey) is { } refusal)
        {
            throw new ProviderRequestRejectedException(refusal);
        }

        return new ProviderStoredCredential(envelope.Fields, envelope.ExpiresAt);
    }

    /// <summary>
    ///     Refuses a handle whose connection has been repointed to another provider family since it was built.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A handle lives as long as the client the host built for a family, which outlives the work that
    ///         built it, and a connection can be repointed while one is held. The credential of the arriving
    ///         family carries the arriving family's identity key and is refused by the envelope check on that
    ///         ground alone, but not when this build has no driver for the arriving family, because such a
    ///         credential records no key and is readable by whoever holds the row. Comparing the stored provider
    ///         identity against the one the handle was built over closes that case without depending on what the
    ///         envelope recorded.
    ///     </para>
    ///     <para>
    ///         What is compared is which family the stored identity names, not how it is spelled. One family has
    ///         more than one spelling — the host's own member name, the key the family declares, and the keys it
    ///         supersedes — and a row rewritten from one of those to another is the same family, not a repoint.
    ///         Comparing the text would refuse every connection of a family whose rows had moved onto its
    ///         declared key, at the point its credential is read.
    ///     </para>
    /// </remarks>
    /// <param name="binding">The connection and family the handle acts for.</param>
    /// <param name="storedIdentity">The provider identity on the row now, or null when the row is gone.</param>
    /// <exception cref="ProviderRequestRejectedException">The row names a different family.</exception>
    internal static void RefuseWhenRepointed(ProviderAddInBinding binding, string? storedIdentity)
    {
        ArgumentNullException.ThrowIfNull(binding);

        if (storedIdentity is null || binding.NamesThisFamily(storedIdentity))
        {
            return;
        }

        throw new ProviderRequestRejectedException(
            $"The connection '{binding.ConnectionDisplayName}' is now served by '{storedIdentity}' and was served "
            + $"by '{binding.ProviderIdentity}' when this credential handle was built. A handle is not carried "
            + "across a change of provider family; the connection has to be resolved again.");
    }

    private async Task RefuseUnlicensedAsync(CancellationToken ct)
    {
        if (await capabilities.IsAvailableAsync(binding.RequiredCapabilityKey, ct).ConfigureAwait(false))
        {
            return;
        }

        throw new ProviderCapabilityUnavailableException(
            $"The provider family '{binding.AddInKey}' requires the '{binding.RequiredCapabilityKey}' capability, "
            + "which this installation's licence does not currently make available.");
    }

    /// <summary>The two columns a credential read needs: what family the row names, and what it holds.</summary>
    private sealed record StoredRow(string ProviderIdentity, string? ProtectedSecret);
}
