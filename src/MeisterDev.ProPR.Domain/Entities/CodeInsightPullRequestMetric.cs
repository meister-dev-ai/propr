// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>
///     The correctness measurement of one pull request, sealed once when the pull request finished. One row per
///     pull request, written at the first close and never rewritten.
/// </summary>
/// <remarks>
///     <para>
///         The counts are stored, not just the ratios. A percentage cannot be re-derived, cannot be
///         re-aggregated, and cannot be explained: rolling a repository up from its pull requests means summing
///         these counts and computing once, and answering "why is recall down" means looking at which count
///         moved. The ratios are carried alongside as a convenience, and must always agree with what the counts
///         produce.
///     </para>
///     <para>
///         Sealed at the first finish, abandon, or close, over resolved findings only. A finding still open when
///         the pull request closed is excluded from the measurement entirely rather than counted as anything (
///         nobody ever said whether it was right) and <see cref="OpenAtSealCount" /> records how many those
///         were, so the exclusion is visible instead of merely implied by a smaller denominator.
///     </para>
///     <para>
///         Immutable once written. A reopen followed by another close leaves the first seal exactly as it was:
///         a number a report has already shown must not move afterwards, and "the measurement at the moment it
///         finished" is only meaningful if it is taken once.
///     </para>
/// </remarks>
public sealed class CodeInsightPullRequestMetric
{
    /// <summary>Unique identifier for this record.</summary>
    public Guid Id { get; init; }

    /// <summary>
    ///     The pull-request aggregate this measurement belongs to. Unique: a pull request is measured once.
    /// </summary>
    public Guid CodeInsightPullRequestId { get; init; }

    /// <summary>Navigation to the owning aggregate.</summary>
    public CodeInsightPullRequest? CodeInsightPullRequest { get; init; }

    /// <summary>
    ///     Owning client, carried on the row itself. The tenancy filter every read applies unconditionally, so
    ///     it must not depend on a join being written correctly.
    /// </summary>
    public Guid ClientId { get; init; }

    /// <summary>Provider repository identifier, carried so a repository roll-up needs no join.</summary>
    public string RepositoryId { get; init; } = string.Empty;

    /// <summary>Provider pull-request identifier.</summary>
    public long PullRequestId { get; init; }

    /// <summary>Findings whose claimed fix was corroborated by a code change.</summary>
    public int AddressedCount { get; init; }

    /// <summary>Findings a human accepted without changing the code.</summary>
    public int AcknowledgedCount { get; init; }

    /// <summary>Findings judged correct but not wanted here. A true positive, and not an acceptance.</summary>
    public int DismissedCount { get; init; }

    /// <summary>Findings judged wrong.</summary>
    public int FalsePositiveCount { get; init; }

    /// <summary>
    ///     Human-raised issues that qualified as something the reviewer should have caught. The false negatives,
    ///     and the only reason recall is measurable at all.
    /// </summary>
    public int MissCount { get; init; }

    /// <summary>
    ///     Findings a human engaged with and left unresolved by the time of the seal: neither accepted nor
    ///     rejected. Recorded and excluded from every ratio, so the volume of undetermined threads is visible
    ///     without being counted as evidence either way.
    /// </summary>
    public int DiscussedCount { get; init; }

    /// <summary>
    ///     Findings that had reached an outcome by the time of the seal. Stored although it is the sum of the
    ///     four disposition counts, because it is the denominator every ratio here was actually divided by.
    ///     <see cref="DiscussedCount" /> is not one of them and is not in this total.
    /// </summary>
    public int ResolvedCount { get; init; }

    /// <summary>
    ///     Findings still open when the pull request closed, and therefore excluded. Recorded so a small
    ///     denominator is explained by the data rather than guessed at.
    /// </summary>
    public int OpenAtSealCount { get; init; }

    /// <summary>
    ///     Of the resolved findings, the share that were right, or <see langword="null" /> when none resolved.
    ///     Null means undefined, never zero.
    /// </summary>
    public double? Precision { get; init; }

    /// <summary>Of the issues that were there to find, the share the reviewer found; null when undefined.</summary>
    public double? Recall { get; init; }

    /// <summary>Harmonic mean of precision and recall; null when either is undefined.</summary>
    public double? F1 { get; init; }

    /// <summary>
    ///     Of the resolved findings, the share a human acted on or agreed with, at the moment of the seal. The
    ///     live acceptance rate is read from the count projection instead: this is the historical value.
    /// </summary>
    public double? AcceptanceRate { get; init; }

    /// <summary>
    ///     The pull-request state observed at the seal, e.g. "Completed" or "Abandoned". All close types seal
    ///     identically; the state is kept so a later question about whether merged and abandoned pull requests
    ///     differ can be answered from the data.
    /// </summary>
    public string CloseState { get; init; } = string.Empty;

    /// <summary>UTC instant the measurement was taken.</summary>
    public DateTimeOffset SealedAt { get; init; }

    /// <summary>
    ///     UTC date of the seal, and the time axis a correctness trend is plotted on: a period's F1 is computed
    ///     from the pull requests that closed in it. Unlike the count projection (which is anchored to review
    ///     time so late outcomes cannot move a past bucket) a seal happens once and never moves, so its own
    ///     date is the stable anchor.
    /// </summary>
    public DateOnly SealedOn { get; init; }
}
