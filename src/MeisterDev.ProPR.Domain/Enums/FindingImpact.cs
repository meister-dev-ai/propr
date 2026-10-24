// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>Consequence of an assessed finding when its occurrence scenario happens.</summary>
public enum FindingImpact
{
    /// <summary>Impact has not been assessed.</summary>
    Unknown = 0,

    /// <summary>Limited local inconvenience or maintenance cost.</summary>
    Low = 1,

    /// <summary>Bounded user or operational disruption.</summary>
    Moderate = 2,

    /// <summary>Substantial service, security, data, or maintenance harm.</summary>
    High = 3,

    /// <summary>Catastrophic service, security, or data harm.</summary>
    Critical = 4,
}
