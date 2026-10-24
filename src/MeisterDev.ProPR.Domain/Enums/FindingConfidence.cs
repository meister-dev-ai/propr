// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>Strength of the evidence that a finding is correct.</summary>
public enum FindingConfidence
{
    /// <summary>Correctness evidence has not been assessed.</summary>
    Unknown = 0,

    /// <summary>Limited supporting evidence with substantial unresolved assumptions.</summary>
    Low = 1,

    /// <summary>Supporting evidence with relevant unresolved assumptions.</summary>
    Medium = 2,

    /// <summary>Strong supporting evidence with minor remaining uncertainty.</summary>
    High = 3,

    /// <summary>Direct deterministic evidence without relevant unresolved assumptions.</summary>
    Certain = 4,
}
