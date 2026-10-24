// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>Occurrence likelihood of a finding's scenario, independent of correctness confidence.</summary>
public enum FindingLikelihood
{
    /// <summary>No occurrence assessment is available.</summary>
    Unknown = 0,

    /// <summary>The scenario requires exceptional conditions.</summary>
    Unlikely = 1,

    /// <summary>The scenario can occur under plausible conditions.</summary>
    Possible = 2,

    /// <summary>The scenario is expected under routine conditions.</summary>
    Likely = 3,

    /// <summary>No meaningful occurrence scenario applies.</summary>
    NotApplicable = 4,
}
