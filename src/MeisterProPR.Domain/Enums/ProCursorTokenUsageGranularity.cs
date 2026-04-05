// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Supported reporting bucket granularities for ProCursor token usage.
/// </summary>
public enum ProCursorTokenUsageGranularity
{
    /// <summary>One bucket per calendar day.</summary>
    Daily = 0,

    /// <summary>One bucket per calendar month.</summary>
    Monthly = 1,
}
