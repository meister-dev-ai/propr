// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>
///     The two kinds of concern a finding can raise: whether the code does the right thing, or whether it can be
///     lived with.
/// </summary>
/// <remarks>
///     <para>
///         A coarser split than the finding taxonomy, and useful precisely because it is coarser. Empirical work
///         on AI review feedback found these two rejected at similar rates for entirely different reasons, which
///         means one combined reason distribution hides what to do about either. A functional finding turned down
///         is usually the reviewer being wrong; an evolvability finding turned down is usually the team not
///         wanting the advice.
///     </para>
///     <para>
///         Derived rather than captured. Every core finding type already names the quality characteristic it
///         belongs to, so this needs no model call and applies to everything already collected.
///     </para>
/// </remarks>
public enum CodeInsightConcernClass
{
    // Persisted nowhere: derived on read from the characteristic a finding's core type carries. The values are
    // explicit anyway, because they cross the wire.

    /// <summary>
    ///     Whether the code does the right thing: correctness, safety, resource use, and speed. A problem a user
    ///     or an attacker could meet.
    /// </summary>
    Functional = 0,

    /// <summary>
    ///     Whether the code can be lived with: structure, naming, documentation, and tests. A problem the next
    ///     person to change it will meet.
    /// </summary>
    Evolvability = 1,
}
