// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>
///     Whether the code the finding is about is absent, present but wrong, or present but unnecessary.
///     This is the defect-qualifier axis orthogonal-defect classification established: "we forgot a null
///     check" and "our null check is wrong" are the same finding type and call for different responses, so
///     a spike in one means something different from a spike in the other.
/// </summary>
public enum CodeInsightFindingQualifier
{
    // Persisted by ordinal: keep these values explicit and do NOT reorder or renumber, or historical
    // findings would silently remap to a different qualifier.

    /// <summary>Something required is absent: a check, a case, a disposal, a test.</summary>
    Missing = 0,

    /// <summary>Something is present but does the wrong thing.</summary>
    Incorrect = 1,

    /// <summary>Something is present that should not be: dead code, a redundant guard, a needless copy.</summary>
    Extraneous = 2,
}
