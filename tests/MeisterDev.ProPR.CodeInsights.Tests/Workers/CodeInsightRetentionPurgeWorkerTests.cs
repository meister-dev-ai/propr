using MeisterDev.ProPR.CodeInsights.Workers;

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.


namespace MeisterDev.ProPR.CodeInsights.Tests.Workers;

public sealed class CodeInsightRetentionPurgeWorkerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ResolveCutoff_SubtractsTheConfiguredWindow()
    {
        var cutoff = CodeInsightRetentionPurgeWorker.ResolveCutoff(30, Now);

        Assert.Equal(Now.AddDays(-30), cutoff);
    }

    [Fact]
    public void ResolveCutoff_DefaultWindowKeepsAtLeastAYearOfHistory()
    {
        // The value of the data is the trend over time, so the default window is deliberately much longer
        // than the review archive's: a short window would make a year-over-year quality trend impossible.
        var cutoff = CodeInsightRetentionPurgeWorker.ResolveCutoff(
            CodeInsightRetentionPurgeWorker.DefaultRetentionDays,
            Now);

        Assert.True(cutoff <= Now.AddDays(-365));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    public void ResolveCutoff_FloorsAMisconfiguredWindowSoFreshDataSurvives(int retentionDays)
    {
        var cutoff = CodeInsightRetentionPurgeWorker.ResolveCutoff(retentionDays, Now);

        // A zero or negative window must not purge data collected moments ago.
        Assert.Equal(Now.AddDays(-1), cutoff);
    }
}
