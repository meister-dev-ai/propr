// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>Risk priority derived from impact and occurrence likelihood.</summary>
public enum FindingRiskSeverity
{
    /// <summary>The assessment cannot be scored.</summary>
    Unknown = 0,

    /// <summary>Low assessed risk.</summary>
    Low = 1,

    /// <summary>Medium assessed risk.</summary>
    Medium = 2,

    /// <summary>High assessed risk.</summary>
    High = 3,

    /// <summary>Critical assessed risk.</summary>
    Critical = 4,
}
