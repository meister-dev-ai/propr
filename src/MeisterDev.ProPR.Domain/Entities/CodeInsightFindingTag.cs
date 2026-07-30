// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.Entities;

/// <summary>
///     One type tag assigned to one finding. A finding may carry several: a missing null check that is also
///     a security hole is genuinely both, and forcing a single choice would lose whichever mattered more.
/// </summary>
/// <remarks>
///     Core and custom assignments are modelled in one table but stay distinguishable through
///     <see cref="IsCore" />, so a cross-client roll-up can exclude custom tags with a single predicate
///     rather than a join. Exactly one of <see cref="CoreSlug" /> and <see cref="CustomTagId" /> is set.
///     A custom assignment points at the tag's identity, never at its name, which is what lets a tag be
///     renamed without relabelling history.
/// </remarks>
public sealed class CodeInsightFindingTag
{
    /// <summary>Unique identifier for this assignment.</summary>
    public Guid Id { get; init; }

    /// <summary>The finding this tag was assigned to.</summary>
    public Guid CodeInsightFindingId { get; init; }

    /// <summary>Navigation to the tagged finding.</summary>
    public CodeInsightFinding? CodeInsightFinding { get; init; }

    /// <summary>Whether this assignment names a core type (comparable across clients) or a custom one.</summary>
    public bool IsCore { get; init; }

    /// <summary>The core type's stable slug when <see cref="IsCore" />; otherwise <see langword="null" />.</summary>
    public string? CoreSlug { get; init; }

    /// <summary>The custom tag's identity when not <see cref="IsCore" />; otherwise <see langword="null" />.</summary>
    public Guid? CustomTagId { get; init; }

    /// <summary>Navigation to the assigned custom tag, when this is a custom assignment.</summary>
    public CodeInsightCustomTag? CustomTag { get; init; }

    /// <summary>
    ///     Version of the core vocabulary in force when the assignment was made, so an assignment stays
    ///     interpretable against the vocabulary that produced it after the core set changes.
    /// </summary>
    public int TaxonomyVersion { get; init; }

    /// <summary>
    ///     Identifier of the classifier that made the assignment, for audit and for re-grading after a
    ///     prompt or model change.
    /// </summary>
    public string ClassifierVersion { get; init; } = string.Empty;

    /// <summary>UTC timestamp when the assignment was made.</summary>
    public DateTimeOffset AssignedAt { get; init; }
}
