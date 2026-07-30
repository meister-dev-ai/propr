// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>
///     The level of code a finding concerns: the highest one it reaches. This is the granularity axis the
///     code-quality literature settles on (a problem local to one method, one inside a type and its
///     collaborators, and one spanning components are different kinds of problem with different costs to
///     fix), collapsed to the levels a reviewer can actually distinguish from a diff.
/// </summary>
/// <remarks>
///     This is NOT <c>ReviewCommentScopeRelation</c>. That records where a finding's anchor sits
///     relative to the pull request's changed lines; this records how much code the problem itself spans.
///     A one-line finding on an unchanged line is <see cref="Statement" /> and
///     <c>OutsideChange</c> at the same time.
/// </remarks>
public enum CodeInsightFindingLevel
{
    // Persisted by ordinal: keep these values explicit and do NOT reorder or renumber, or historical
    // findings would silently remap to a different level.

    /// <summary>A single statement or expression: the problem is contained in the line or two cited.</summary>
    Statement = 0,

    /// <summary>One method, property, or function body.</summary>
    Member = 1,

    /// <summary>A type and its members: responsibility placement, invariants, state across methods.</summary>
    Type = 2,

    /// <summary>A whole file or module beyond a single type.</summary>
    File = 3,

    /// <summary>
    ///     Several files, a component, or the architecture: coupling, layering, and cross-cutting concerns.
    ///     Pull-request-wide findings normally land here.
    /// </summary>
    CrossFile = 4,
}
