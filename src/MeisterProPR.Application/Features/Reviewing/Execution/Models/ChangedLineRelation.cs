// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Deterministic classification of where a review finding's anchor line falls relative to the
///     pull request's changed-line ranges for its file. A finding whose line cannot be classified
///     (unknown line or a file with no resolvable changed ranges) carries no relation at all.
/// </summary>
public enum ChangedLineRelation
{
    /// <summary>
    ///     The finding's line falls inside one of the file's changed ranges.
    /// </summary>
    OnChangedLine,

    /// <summary>
    ///     The finding's line is within a small neighborhood of a changed range. Context lines next to an
    ///     edit are treated as part of the change, so this is considered in-scope and carries no label.
    /// </summary>
    AdjacentToChange,

    /// <summary>
    ///     The finding's line lies in code far from every changed range — pre-existing code outside the
    ///     pull request's edits. Findings here are still published but are labeled as out of scope.
    /// </summary>
    OutsideChange,
}
