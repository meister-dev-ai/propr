// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Hosting;

/// <summary>
///     Exclusive access to one connection's stored credential for the length of a read, an exchange with the
///     vendor, and a write, all inside one transaction.
/// </summary>
/// <remarks>
///     <para>
///         The three steps are one primitive rather than three calls because the property that matters is that
///         they share a lock and a transaction. Exactly one exchange then happens per expiry however many callers
///         arrive at once, and the callers that lost read back the winner's credential instead of each starting
///         an exchange of their own; with a rotating credential, two exchanges lose it permanently.
///     </para>
///     <para>
///         The session owns its own unit of work rather than joining whatever the caller was doing, so it is safe
///         to open from several threads at once — which is the ordinary case, since a review runs passes in
///         parallel against one connection.
///     </para>
///     <para>
///         A family sees named values, never the stored envelope: the host protects and encodes what it is
///         given. It also never chooses which connection this is for, because a session is handed out already
///         bound to one.
///     </para>
/// </remarks>
public interface IProviderCredentialSession : IAsyncDisposable
{
    /// <summary>
    ///     Reads the stored credential under the lock this session holds, by the field names the family declared
    ///     for its mode.
    /// </summary>
    /// <remarks>
    ///     Read here rather than taken from whatever the caller read before opening the session, because the
    ///     point of the lock is that the value can have changed in between: a caller that waited for the lock
    ///     while another renewed the credential reads the renewed one and has nothing left to do.
    /// </remarks>
    /// <param name="ct">Cancels the read.</param>
    Task<ProviderStoredCredential> ReadAsync(CancellationToken ct = default);

    /// <summary>Replaces the stored credential with the fields supplied.</summary>
    /// <remarks>
    ///     The expiry is required and has to be in the future. A credential stored without one is treated as
    ///     never usable, so accepting one would renew before every call and serialise a whole review through this
    ///     lock; the host refuses it and names the field.
    /// </remarks>
    /// <param name="fields">The credential fields, by the names the family declared.</param>
    /// <param name="expiresAt">When the credential stops being usable.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <exception cref="ProviderRequestRejectedException">
    ///     The expiry is absent or already past, or the fields exceed what the host stores.
    /// </exception>
    Task WriteAsync(
        IReadOnlyDictionary<string, string> fields,
        DateTimeOffset expiresAt,
        CancellationToken ct = default);

    /// <summary>Removes the stored credential, leaving the connection itself in place.</summary>
    /// <remarks>
    ///     What a family runs when an operator disconnects an account: the connection keeps its address, its
    ///     configured models and its declared values, and holds no credential, which is the state it was in
    ///     before the credential flow was run against it. What the host recorded about that credential — the
    ///     health last observed for it and the administrator who authorized it — is removed with it, because both
    ///     describe a credential that is no longer there.
    /// </remarks>
    /// <param name="ct">Cancels the removal.</param>
    /// <exception cref="ProviderRequestRejectedException">The connection no longer exists.</exception>
    Task ClearAsync(CancellationToken ct = default);

    /// <summary>
    ///     Commits the work done in this session. A session disposed without this is rolled back, which 
    ///     leaves a failed exchange with the credential it started from rather than with a half-written one.
    /// </summary>
    /// <param name="ct">Cancels the commit.</param>
    Task CompleteAsync(CancellationToken ct = default);
}

/// <summary>
///     Reaches the one connection's stored credential this handle is bound to, either to read what is there or to
///     take it exclusively for a renewal.
/// </summary>
/// <remarks>
///     <para>
///         A family holds this for as long as it holds the client the host built for it, which outlives the work
///         that built it: a review is a long tool-calling loop against a client constructed once, and its later
///         passes run after the credential the client started with has expired. Nothing here is resolved from the
///         work that built the client, and every call may be made from every thread the client serves.
///     </para>
///     <para>
///         Both calls check that the installation is still licensed for the family before they answer, which is
///         the one licence check on the review path: the checks made when a connection is configured fire on
///         configuration and never on use, so without this one an installation whose entitlement lapsed keeps
///         running reviews on the credentials it already stored.
///     </para>
/// </remarks>
public interface IProviderCredentialSessions
{
    /// <summary>
    ///     Reads the stored credential without taking the row, which a family does on the ordinary call
    ///     where the credential is still good.
    /// </summary>
    /// <remarks>
    ///     No lock, so the calls a review makes in parallel do not queue behind each other. A family that finds
    ///     the credential expiring opens a session and reads again under the lock.
    /// </remarks>
    /// <param name="ct">Cancels the read.</param>
    /// <exception cref="ProviderCapabilityUnavailableException">The installation is no longer licensed for this family.</exception>
    Task<ProviderStoredCredential> ReadAsync(CancellationToken ct = default);

    /// <summary>
    ///     Takes the connection's credential row and holds it until the session is disposed. A caller that waits
    ///     longer than the host allows is told the wait failed, which the retry stage treats as transient: a slow
    ///     vendor exchange is a retry and not a review error.
    /// </summary>
    /// <param name="ct">Cancels waiting for the row.</param>
    /// <exception cref="ProviderCapabilityUnavailableException">The installation is no longer licensed for this family.</exception>
    /// <exception cref="ProviderHostTimeoutException">Another caller held the row for longer than the host waits.</exception>
    Task<IProviderCredentialSession> OpenAsync(CancellationToken ct = default);
}
