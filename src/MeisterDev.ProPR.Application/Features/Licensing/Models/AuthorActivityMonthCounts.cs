// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>The counted and excluded authors recorded for one UTC calendar month.</summary>
public sealed record AuthorActivityMonthCounts
{
    /// <summary>
    ///     Creates the counts one month holds.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     Thrown when <paramref name="month" /> is not the first day of a month, or either count is negative.
    /// </exception>
    public AuthorActivityMonthCounts(DateOnly month, long counted, long excluded)
    {
        if (month.Day != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(month), month, "The month must be the first day of the month.");
        }

        if (counted < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(counted), counted, "The counted authors cannot be negative.");
        }

        if (excluded < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(excluded), excluded, "The excluded authors cannot be negative.");
        }

        this.Month = month;
        this.Counted = counted;
        this.Excluded = excluded;
    }

    /// <summary>
    ///     The first day of the month the counts cover, in UTC. A date rather than an instant, because the unit
    ///     is the month and no part of the day within it carries meaning.
    /// </summary>
    public DateOnly Month { get; }

    /// <summary>
    ///     Distinct authors the month holds, excluded ones left out. Held as a 64-bit count, matching the width
    ///     the database reports a count in and the width the other licensing counts carry.
    /// </summary>
    public long Counted { get; }

    /// <summary>
    ///     Distinct authors the month holds that the exclusion rules kept out of <see cref="Counted" />. Carried
    ///     beside it so an operator can see how many identities the exclusions account for; the two together are
    ///     every account the month holds.
    /// </summary>
    public long Excluded { get; }
}
