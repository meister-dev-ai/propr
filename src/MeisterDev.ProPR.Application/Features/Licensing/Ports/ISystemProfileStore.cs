// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Application.Features.Licensing.Ports;

/// <summary>
///     Persistence boundary for the installation's observed profile, the changes recorded against it and the
///     host names it has been seen running on.
///     <para>
///         Replicas observe independently and reach the same stable components. The two write paths that change
///         the profile row therefore report whether this caller's write was the one that made the change, so a
///         change is recorded once rather than once per replica that noticed it. On PostgreSQL that answer comes
///         from the row itself, through a conditional insert and a conditional update. The in-memory test host
///         reads the row and then writes it, which settles callers that do not overlap and nothing beyond that.
///     </para>
/// </summary>
public interface ISystemProfileStore
{
    /// <summary>Reads the profile the installation currently reports.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The profile, or <see langword="null" /> when none has been captured yet.</returns>
    Task<SystemProfileSnapshot?> GetCurrentAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Records the installation's first profile.
    ///     <para>
    ///         On PostgreSQL the answer comes from an insert that keeps the row already on file, so exactly one
    ///         of several replicas starting together is told it captured the baseline. The in-memory test host
    ///         checks for the row and then inserts.
    ///     </para>
    /// </summary>
    /// <param name="snapshot">The observed profile.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true" /> when this caller captured it, <see langword="false" /> when another already had.</returns>
    Task<bool> TryCaptureAsync(SystemProfileSnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Replaces the profile and records the change, on the condition that the row still carries the hash
    ///     the caller observed it under.
    ///     <para>
    ///         Both writes happen together, so a profile that moved always carries a record of the move: an
    ///         interruption between them would otherwise leave a hash nothing accounts for. On PostgreSQL the
    ///         conditional update and the record are one transaction, and the condition is what makes exactly one
    ///         of several replicas the caller that records the change. The in-memory test host reads, compares
    ///         and writes both rows in one save, which keeps the record with the move but decides nothing between
    ///         two callers writing at once.
    ///     </para>
    /// </summary>
    /// <param name="expectedProfileHash">The hash the caller read before observing the change.</param>
    /// <param name="snapshot">The observed profile.</param>
    /// <param name="drift">The change to record alongside it.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns><see langword="true" /> when this caller's write made the change.</returns>
    Task<bool> TryRecordChangeAsync(
        string expectedProfileHash,
        SystemProfileSnapshot snapshot,
        SystemProfileDrift drift,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Records what the host looks like now and that the profile was observed, leaving the stable
    ///     components and the profile hash alone.
    /// </summary>
    /// <param name="components">The observed volatile components.</param>
    /// <param name="observedAt">When the observation was made.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the row has been updated.</returns>
    Task UpdateVolatileAsync(
        SystemProfileVolatileComponents components,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the recorded changes, newest first.</summary>
    /// <param name="limit">How many records to return at most.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The recorded changes.</returns>
    Task<IReadOnlyList<SystemProfileDrift>> ListDriftAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>Merges an observed host name into the set, extending its last-seen instant.</summary>
    /// <param name="hostname">The host name the replica reported.</param>
    /// <param name="observedAt">When it was observed.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the host name has been recorded.</returns>
    Task RecordHostnameAsync(
        string hostname,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the host names the installation has been observed running on, most recent first.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The host names.</returns>
    Task<IReadOnlyList<ReplicaHostname>> ListHostnamesAsync(CancellationToken cancellationToken = default);
}
