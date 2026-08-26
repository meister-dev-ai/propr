// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>
///     One calendar month whose counted authors went above the number the license states, keyed by the month so
///     a month holds one row however often it was observed above the number.
/// </summary>
public sealed class LicensingAuthorOverageRecord
{
    /// <summary>The first day of the UTC month this row covers.</summary>
    public DateOnly OverageMonth { get; set; }

    /// <summary>The number the license stated when the month was first observed above it.</summary>
    public long LicensedCount { get; set; }

    /// <summary>The highest author count observed in the month.</summary>
    public long HighestObservedCount { get; set; }

    /// <summary>When the month was first observed above the number.</summary>
    public DateTimeOffset FirstObservedAt { get; set; }

    /// <summary>When the month was last observed above the number.</summary>
    public DateTimeOffset LastObservedAt { get; set; }
}
