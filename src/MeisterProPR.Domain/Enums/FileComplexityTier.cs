// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Complexity tier assigned to a changed file based on its diff size.
///     Used to select an appropriate per-file AI iteration budget and model connection.
/// </summary>
public enum FileComplexityTier
{
    /// <summary>Small diff (≤30 changed lines). Uses <c>MaxIterationsLow</c> budget.</summary>
    Low = 0,

    /// <summary>Medium diff (31–150 changed lines). Uses <c>MaxIterationsMedium</c> budget.</summary>
    Medium = 1,

    /// <summary>Large diff (&gt;150 changed lines). Uses <c>MaxIterationsHigh</c> budget.</summary>
    High = 2,
}
