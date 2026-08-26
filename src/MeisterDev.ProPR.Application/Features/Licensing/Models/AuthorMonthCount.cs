// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     How many distinct authors one calendar month holds.
/// </summary>
public sealed record AuthorMonthCount
{
    /// <summary>
    ///     Creates a monthly author count.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     Thrown when <paramref name="month" /> is not the first day of a month or <paramref name="authorCount" />
    ///     is negative.
    /// </exception>
    public AuthorMonthCount(DateOnly month, long authorCount)
    {
        if (month.Day != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(month), month, "The month must be the first day of the month.");
        }

        if (authorCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(authorCount), authorCount, "The author count cannot be negative.");
        }

        this.Month = month;
        this.AuthorCount = authorCount;
    }

    /// <summary>
    ///     The first day of the month the count covers, in UTC. A date rather than an instant, because the unit
    ///     is the month and no part of the day within it carries meaning.
    /// </summary>
    public DateOnly Month { get; }

    /// <summary>
    ///     Distinct authors the month holds, excluded ones left out. Held as a 64-bit count, matching the width
    ///     the database reports a count in and the width the other licensing counts carry.
    /// </summary>
    public long AuthorCount { get; }
}
