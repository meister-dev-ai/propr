// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Application.Features.Licensing.Ports;

/// <summary>
///     Persistence boundary for the one license document an installation has activated.
///     <para>
///         The store holds a document and hands it back; it verifies nothing. Protecting the document at rest
///         and reading it back are the store's concern, which is why a value that cannot be read back is
///         reported through <see cref="StoredLicense.IsReadable" /> rather than raised.
///     </para>
///     <para>
///         A caller that writes through this store calls <see cref="ILicenseStateProvider.Invalidate" />
///         afterwards, because the provider caches the state it verified and would otherwise keep serving the
///         previous answer in this process. Other replicas pick the change up when their own copy ages out.
///     </para>
/// </summary>
public interface IActivatedLicenseStore
{
    /// <summary>Reads the activated license, or <see langword="null" /> when none has been activated.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The stored license, or <see langword="null" />.</returns>
    Task<StoredLicense?> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Replaces the activated document and reports what it displaced, together with the instant the store
    ///     carried the replacement out.
    /// </summary>
    /// <remarks>
    ///     On PostgreSQL the read of the previous document and the replacement are one mutation, held under a
    ///     transaction-scoped lock on the singleton row. Callers use the returned value to record whether this
    ///     activation replaced a document, rather than making a separate read whose answer a concurrent replica
    ///     could invalidate. The in-memory test host reads and writes without that lock, so it reports the same
    ///     value for a single caller but establishes nothing against a concurrent one.
    /// </remarks>
    /// <param name="compactLicense">The compact license document.</param>
    /// <param name="activatedByUserId">Who activated it, when a signed-in user did.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>
    ///     The displaced document, or <see langword="null" /> within the result when none was on file, with
    ///     the instant the store observed for the mutation.
    /// </returns>
    Task<LicenseMutation> ReplaceAsync(
        string compactLicense,
        Guid? activatedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Stores a license document, replacing whatever was activated before. An installation holds one
    ///     license, so this is a single-row write. On PostgreSQL it is an upsert under the same lock
    ///     <see cref="ReplaceAsync" /> takes, which is what lets two replicas run it at the same time without
    ///     either failing; the in-memory test host writes without that lock.
    /// </summary>
    /// <param name="compactLicense">The compact license document.</param>
    /// <param name="activatedByUserId">Who activated it, when a signed-in user did.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the document is stored.</returns>
    Task SetAsync(string compactLicense, Guid? activatedByUserId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes the activated document and reports what was removed, together with the instant the store
    ///     carried the removal out.
    /// </summary>
    /// <remarks>
    ///     On PostgreSQL the read and the deletion are one mutation under the same lock the replacement takes,
    ///     so a document activated by another replica cannot be deleted after a caller identified an earlier
    ///     document for its history record. The in-memory test host does not take that lock.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>
    ///     The removed document, or <see langword="null" /> within the result when none was on file, with the
    ///     instant the store observed for the mutation.
    /// </returns>
    Task<LicenseMutation> RemoveAndGetAsync(CancellationToken cancellationToken = default);

    /// <summary>Removes the activated license. Removing when none is activated is not an error.</summary>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when no license is on file.</returns>
    Task RemoveAsync(CancellationToken cancellationToken = default);
}
