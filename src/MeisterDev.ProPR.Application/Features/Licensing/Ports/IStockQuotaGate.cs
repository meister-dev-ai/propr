// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Application.Features.Licensing.Ports;

/// <summary>
///     Decides whether one more of a counted resource fits under its ceiling, and whether one the installation
///     already holds still does.
///     <para>
///         A stock quota bounds how many rows of a kind the installation holds: clients, enrolled runners. The
///         decision cannot be a count followed by an insert, because two creations that read the same count both
///         pass a ceiling they jointly exceed. Every creation site asks here instead, so one rule decides
///         admission and a refusal names the same numbers wherever it is raised.
///     </para>
///     <para>
///         A resource whose place is re-authorized after it was created asks the second question at that point,
///         so a ceiling lowered after the fact reaches the rows that already exist rather than binding creations
///         only.
///     </para>
///     <para>
///         Reviews executing at once are not admitted here. That quota is bounded inside the statement that
///         claims a review, where the count and the state change are one statement rather than two.
///     </para>
///     <para>
///         The ceiling comes from <see cref="ILicenseLimitResolver" />. Nothing here reads a license value
///         directly, so the number a creation is refused against is the number the rest of the installation
///         reports.
///     </para>
///     <para>
///         The transaction contract: an admission that had to be serialized holds the database transaction it
///         was decided in, and the serialization lasts as long as that transaction. A caller therefore admits,
///         creates the row, and commits the admission, in that order, and disposes the admission whether or not
///         it committed. The transaction is opened on the scoped context injected into the request, so a creation
///         that saves through that context lands in it without the caller arranging anything. A context opened
///         from an <c>IDbContextFactory</c> runs on a different connection and does not join the admission.
///         Disposing an admission that was not committed discards the creation with it.
///     </para>
///     <para>
///         The transaction covers everything the context saves while the admission is open, including changes
///         tracked before it was asked, so a caller saves nothing it wants to keep independent of the creation.
///         After an admission is disposed without a commit the context still tracks the discarded rows as
///         stored, so it is not used for further writes in that request.
///     </para>
///     <para>
///         A caller that already opened a transaction keeps it. The gate decides inside that transaction and
///         commits nothing, so the caller's own commit ends it and releases the serialization. A refusal decided
///         inside a caller-owned transaction also holds the serialization until that transaction ends, so a
///         caller ends it before reporting.
///     </para>
///     <para>
///         A second admission asked for inside the same transaction joins it and commits nothing, on the same
///         terms as any other caller-owned transaction. A caller that needs two quotas at once acquires them in
///         a fixed order, because two callers waiting on the same pair in opposite orders end with PostgreSQL
///         aborting one of them with a deadlock error.
///     </para>
///     <para>
///         The count is read after the serialization is granted. The installation runs at READ COMMITTED, so
///         that count includes a row the previous holder committed; a snapshot fixed before the wait would count
///         the same row as absent. A caller that opens the transaction therefore opens it at READ COMMITTED: a
///         higher isolation level fixes the snapshot before the lock is waited for, and the count taken
///         afterwards no longer reflects what the previous holder wrote.
///     </para>
/// </summary>
public interface IStockQuotaGate
{
    /// <summary>Decides whether one more of a counted resource may be created.</summary>
    /// <param name="key">The stock quota to decide against.</param>
    /// <param name="cancellationToken">Cancels the decision.</param>
    /// <returns>
    ///     The admission, which the caller commits after the creation has been saved and disposes either way.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">The key is not a quota counted as stored rows.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The ceiling resolved for the key is one no row count can be compared against.
    /// </exception>
    Task<StockQuotaAdmission> AdmitOneAsync(
        LicenseLimitKey key,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Decides whether one of a counted resource the installation already holds still fits under its
    ///     ceiling.
    ///     <para>
    ///         A separate question from <see cref="AdmitOneAsync" /> because the subject is inside the count.
    ///         A creation is refused once the count has reached the ceiling, since the row it would add takes
    ///         the count past it. A resource that is already counted is refused only once the count has passed
    ///         the ceiling, because at the ceiling it is one of the places the license grants. Asking the
    ///         creation question about an existing resource would refuse every one of them on an installation
    ///         sitting exactly at its ceiling, which is the state of a fully used license.
    ///     </para>
    ///     <para>
    ///         The transaction contract is the one <see cref="AdmitOneAsync" /> states: the caller saves its
    ///         change, commits the admission, and disposes it either way.
    ///     </para>
    /// </summary>
    /// <param name="key">The stock quota to decide against.</param>
    /// <param name="cancellationToken">Cancels the decision.</param>
    /// <returns>
    ///     The admission, which the caller commits after its change has been saved and disposes either way.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">The key is not a quota counted as stored rows.</exception>
    /// <exception cref="InvalidOperationException">
    ///     The ceiling resolved for the key is one no row count can be compared against.
    /// </exception>
    Task<StockQuotaAdmission> AdmitExistingAsync(
        LicenseLimitKey key,
        CancellationToken cancellationToken = default);
}
