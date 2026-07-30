// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>
///     What became of one finding, recorded when its review thread resolved. Exactly one per finding, and
///     once decided it stays decided: a metric computed from a disposition must not change underneath a
///     report because the same thread was observed again.
/// </summary>
/// <remarks>
///     The source signals are stored alongside the verdict rather than discarded, so a disagreement about a
///     disposition can be settled by looking at what it was derived from instead of by re-running a
///     classifier over a thread that has since moved on.
/// </remarks>
public sealed class CodeInsightFindingDisposition
{
    /// <summary>Unique identifier for this record.</summary>
    public Guid Id { get; init; }

    /// <summary>
    ///     The finding this disposition belongs to. Unique: a finding has exactly one outcome.
    /// </summary>
    public Guid CodeInsightFindingId { get; init; }

    /// <summary>Navigation to the finding.</summary>
    public CodeInsightFinding? CodeInsightFinding { get; init; }

    /// <summary>What became of the finding.</summary>
    public CodeInsightDisposition Disposition { get; init; }

    /// <summary>
    ///     The provider-neutral meaning of the thread's close, as the crawl reported it. A source signal.
    /// </summary>
    public ThreadResolutionIntent SourceIntent { get; init; }

    /// <summary>
    ///     Whether the anchored code had changed since the finding was raised, as the crawl reported it.
    ///     A source signal, and the thing that separates a claimed fix from a corroborated one.
    /// </summary>
    public ThreadAnchorCodeChange SourceCodeChange { get; init; }

    /// <summary>
    ///     Identifier of the classifier that produced the wrong-versus-not-relevant split, or
    ///     <see langword="null" /> when the disposition was derived deterministically and no classifier ran.
    /// </summary>
    public string? ClassifierVersion { get; init; }

    /// <summary>
    ///     The classifier's confidence in the split, 0–1, or <see langword="null" /> when no classifier ran.
    /// </summary>
    public double? ClassifierConfidence { get; init; }

    /// <summary>
    ///     Why the finding was rejected, when it was rejected and the reason could be judged.
    ///     <see langword="null" /> in three cases that must stay distinguishable from each other: the finding
    ///     was not rejected at all, the classifier could judge the rejection but not its reason, or the
    ///     disposition was decided before reasons were recorded. None of them is a reason, so none of them may
    ///     be counted as one.
    /// </summary>
    public CodeInsightRejectionReason? RejectionReason { get; init; }

    /// <summary>UTC timestamp when the disposition was decided.</summary>
    public DateTimeOffset DecidedAt { get; init; }
}
