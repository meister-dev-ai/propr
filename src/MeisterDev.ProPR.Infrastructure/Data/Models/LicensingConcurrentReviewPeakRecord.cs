// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>
///     The highest number of reviews observed executing at the same time on one UTC day, keyed by the day so a
///     day holds one row however many claims observed it.
/// </summary>
public sealed class LicensingConcurrentReviewPeakRecord
{
    /// <summary>The UTC day this row covers.</summary>
    public DateOnly PeakDate { get; set; }

    /// <summary>The highest number of reviews observed executing at the same time on that day.</summary>
    public long PeakCount { get; set; }

    /// <summary>When the row last took a higher count.</summary>
    public DateTimeOffset ObservedAt { get; set; }
}
