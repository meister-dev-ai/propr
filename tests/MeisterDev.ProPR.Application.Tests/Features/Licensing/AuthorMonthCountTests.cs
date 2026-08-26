// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;

namespace MeisterDev.ProPR.Application.Tests.Features.Licensing;

/// <summary>
///     The month and count carried by a trailing-year author peak.
/// </summary>
public sealed class AuthorMonthCountTests
{
    [Fact]
    public void AMonthStartAndNonnegativeCount_AreKept()
    {
        var count = new AuthorMonthCount(new DateOnly(2026, 4, 1), 19);

        Assert.Equal(new DateOnly(2026, 4, 1), count.Month);
        Assert.Equal(19, count.AuthorCount);
    }

    [Fact]
    public void ADateAfterTheFirstDayOfTheMonth_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AuthorMonthCount(new DateOnly(2026, 4, 15), 19));
    }

    [Fact]
    public void ANegativeCount_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AuthorMonthCount(new DateOnly(2026, 4, 1), -1));
    }
}
