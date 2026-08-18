// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.UsageStatistics.Support;

namespace MeisterDev.ProPR.Application.Tests.Features.UsageStatistics;

public sealed class UsageStatisticsBucketsTests
{
    // The boundaries are published, so each one is pinned on both sides. A widened bucket changes what the
    // payload documentation states is collected.
    [Theory]
    [InlineData(0, "1")]
    [InlineData(1, "1")]
    [InlineData(2, "2-5")]
    [InlineData(5, "2-5")]
    [InlineData(6, "6-20")]
    [InlineData(20, "6-20")]
    [InlineData(21, "21-50")]
    [InlineData(50, "21-50")]
    [InlineData(51, "50+")]
    [InlineData(10_000, "50+")]
    public void AnAccountCount_LandsInItsPublishedBucket(int count, string expected)
    {
        Assert.Equal(expected, UsageStatisticsBuckets.ForActiveUsers(count));
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(1, "1-20")]
    [InlineData(20, "1-20")]
    [InlineData(21, "21-100")]
    [InlineData(100, "21-100")]
    [InlineData(101, "101-500")]
    [InlineData(500, "101-500")]
    [InlineData(501, "500+")]
    public void APullRequestRate_LandsInItsPublishedBucket(double perWeek, string expected)
    {
        Assert.Equal(expected, UsageStatisticsBuckets.ForWeeklyPullRequests(perWeek));
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(1, "1-50")]
    [InlineData(50, "1-50")]
    [InlineData(51, "51-250")]
    [InlineData(250, "51-250")]
    [InlineData(251, "251-1000")]
    [InlineData(1000, "251-1000")]
    [InlineData(1001, "1000+")]
    public void AFindingRate_LandsInItsPublishedBucket(double perWeek, string expected)
    {
        Assert.Equal(expected, UsageStatisticsBuckets.ForWeeklyFindings(perWeek));
    }

    // A normalised rate is fractional. Rounding before bucketing keeps the published boundaries whole counts.
    [Theory]
    [InlineData(0.4, "0")]
    [InlineData(0.5, "1-20")]
    [InlineData(20.4, "1-20")]
    [InlineData(20.5, "21-100")]
    public void AFractionalRate_RoundsBeforeItIsBucketed(double perWeek, string expected)
    {
        Assert.Equal(expected, UsageStatisticsBuckets.ForWeeklyPullRequests(perWeek));
    }

    // A rate is produced by a division, so its degenerate results must not produce a label outside the
    // published set.
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1d)]
    public void ARateThatIsNotANumber_ReportsTheEmptyBucket(double perWeek)
    {
        Assert.Equal("0", UsageStatisticsBuckets.ForWeeklyFindings(perWeek));
    }

    [Fact]
    public void AnUnboundedRate_ReportsTheTopBucket()
    {
        Assert.Equal("1000+", UsageStatisticsBuckets.ForWeeklyFindings(double.PositiveInfinity));
    }

    // Every label a counter can produce must appear in the published list, which the payload documentation
    // enumerates.
    [Fact]
    public void EveryProducedLabel_AppearsInThePublishedSet()
    {
        var produced = new List<string>();
        for (var count = 0; count <= 120; count++)
        {
            produced.Add(UsageStatisticsBuckets.ForActiveUsers(count));
        }

        Assert.All(produced, label => Assert.Contains(label, UsageStatisticsBuckets.ActiveUserLabels));

        var rates = new List<string>();
        for (var count = 0; count <= 1200; count++)
        {
            rates.Add(UsageStatisticsBuckets.ForWeeklyFindings(count));
            Assert.Contains(UsageStatisticsBuckets.ForWeeklyPullRequests(count), UsageStatisticsBuckets.PullRequestLabels);
        }

        Assert.All(rates, label => Assert.Contains(label, UsageStatisticsBuckets.FindingLabels));
    }
}
