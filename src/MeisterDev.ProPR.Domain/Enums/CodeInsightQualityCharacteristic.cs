// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>
///     The product-quality characteristic a finding type contributes to. These are the four
///     source-code-level characteristics the ISO/IEC 25010 product quality model defines and the
///     industry's automated code-quality measures report on, so a roll-up by characteristic reads the
///     same way as the quality reporting an organisation already does.
///     Each core finding type maps to exactly one of these; the mapping lives in the versioned core
///     taxonomy so a characteristic roll-up needs no second classification.
/// </summary>
public enum CodeInsightQualityCharacteristic
{
    // Persisted by ordinal wherever a characteristic is stored: keep these values explicit and do NOT
    // reorder or renumber, or historical rows would silently remap to a different characteristic.

    /// <summary>The code's ability to perform correctly and recover from faults.</summary>
    Reliability = 0,

    /// <summary>Protection of the software from unauthorised access, use, or destruction.</summary>
    Security = 1,

    /// <summary>How well the code uses time, resources, and system capacity.</summary>
    PerformanceEfficiency = 2,

    /// <summary>The ease with which the code can be understood, modified, and repaired.</summary>
    Maintainability = 3,
}
