// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     What a host may actually run at once, as opposed to what it was configured to run.
///     <para>
///         Review work fans out on two independent axes that multiply: how many jobs one host runs at once,
///         and how many files one job reviews at once. Both are licensed by the same capability, so both are
///         held to one when it is absent. Otherwise an unlicensed host still fans out N-wide inside a single
///         review and the one-at-a-time rule only looks enforced.
///     </para>
/// </summary>
public static class ReviewConcurrencyPolicy
{
    /// <summary>What either axis is held to without the parallel-execution capability.</summary>
    public const int Unlicensed = 1;

    /// <summary>
    ///     The configured width when parallel review execution is licensed, one when it is not.
    /// </summary>
    /// <param name="configured">The width configured for this axis.</param>
    /// <param name="parallelReviewExecutionEnabled">Whether the capability is available to this host.</param>
    public static int Effective(int configured, bool parallelReviewExecutionEnabled)
    {
        return parallelReviewExecutionEnabled ? configured : Unlicensed;
    }
}
