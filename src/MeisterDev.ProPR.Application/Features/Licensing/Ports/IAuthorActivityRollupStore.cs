// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Licensing.Ports;

/// <summary>
///     The rollup of authors ProPR did work for, one row per calendar month and author, plus the reads over it.
///     <para>
///         The rollup exists so the count never scans the job tables. A month's authors are written as the work
///         completes and read back as rows, which keeps the read a bounded count on one small table however
///         many reviews and answers the installation has behind it.
///     </para>
///     <para>
///         An author is named by the host-scoped key from <see cref="ProviderHostRef.ScopedKey" />, so two hosts
///         that issue the same identifier stay two people, and one person who both opens pull requests and asks
///         questions is one author.
///     </para>
///     <para>
///         The count can miss an author whose only work in a month was an in-process review that produced no
///         findings, because that flow deletes the job row rather than completing it and so never reaches the
///         write. The same review performed on a runner is counted. The difference undercounts, which is the
///         direction that favors the installation.
///     </para>
///     <para>
///         Which authors are excluded is decided by the caller, on the signals the finished work carried, and
///         the decision applies from that write forward. Rows written before a rule existed keep the flag they
///         were written with; nothing rewrites them, and the only way an existing row's flag changes is the
///         escalation on a later observation of the same author in the same month.
///     </para>
/// </summary>
public interface IAuthorActivityRollupStore
{
    /// <summary>
    ///     Puts an author into the current UTC month, and raises the month's exclusion flag for them when this
    ///     observation was of automation.
    /// </summary>
    /// <remarks>
    ///     The month comes from the database clock, so every replica agrees on which month a completion falls
    ///     in whatever its own clock reads. The row records the observation; it never counts it, so the same
    ///     author observed a hundred times in a month is one row and one author.
    ///     <para>
    ///         The exclusion flag only ever rises. An account observed as automation once in a month stays
    ///         excluded for that month, because the later observation of it as a person does not undo what the
    ///         earlier signal stated. Everything else about the row stays as it was first written.
    ///     </para>
    /// </remarks>
    /// <param name="host">The host that issued the identifier.</param>
    /// <param name="externalUserId">The author's identifier as that host issues it.</param>
    /// <param name="source">Which kind of finished work made the observation.</param>
    /// <param name="excluded">Whether this observation identified the author as automation.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the month holds the author.</returns>
    Task RecordAuthorAsync(
        ProviderHostRef host,
        string externalUserId,
        AuthorActivitySource source,
        bool excluded,
        CancellationToken cancellationToken = default);

    /// <summary>Counts the distinct authors the current UTC month holds, excluded ones left out.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The count, zero when the month holds none.</returns>
    Task<int> CountCurrentMonthAuthorsAsync(CancellationToken cancellationToken = default);

    /// <summary>Counts the authors the current UTC month holds as excluded.</summary>
    /// <remarks>
    ///     Reported beside the counted authors so an operator can see how many identities the exclusion rules
    ///     kept out of the number. It is the complement of the counted authors over the same month, so the two
    ///     together are every account the month holds.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The count, zero when the month holds no excluded author.</returns>
    Task<int> CountCurrentMonthExcludedAuthorsAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads both author counts against one captured UTC month.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The month and its counted and excluded authors.</returns>
    Task<AuthorActivityMonthCounts> GetCurrentMonthCountsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     The busiest of the twelve UTC months ending with the current one, and how many authors it held.
    /// </summary>
    /// <remarks>
    ///     Twelve months including the current one, so the answer moves with the installation rather than
    ///     reporting a peak the licensed period no longer covers. Months that hold nothing after exclusions are
    ///     not candidates; an installation with no counted author in the whole window has no peak.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The peak month, or <see langword="null" /> when the window holds no counted author.</returns>
    Task<AuthorMonthCount?> GetTrailingYearPeakAsync(CancellationToken cancellationToken = default);
}
