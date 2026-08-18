// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.UsageStatistics.Models;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Ports;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Services;
using MeisterDev.ProPR.Application.Features.UsageStatistics.Support;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.UsageStatistics;

public sealed class UsageStatisticsSnapshotBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ASnapshot_CarriesTheInstallationIdentityVersionAndEdition()
    {
        var state = UsageStatisticsTestDoubles.EnabledState(Now);
        var builder = Build(new UsageStatisticsCounts(3, 4, 5, 6, 7), "1.2.3");

        var snapshot = await builder.BuildAsync(state, UsageStatisticsEdition.Commercial);

        Assert.Equal(UsageStatisticsContract.SchemaVersion, snapshot.SchemaVersion);
        Assert.Equal(state.InstanceId, snapshot.InstanceId);
        Assert.Equal("1.2.3", snapshot.ProductVersion);
        Assert.Equal(UsageStatisticsEdition.Commercial, snapshot.Edition);
    }

    // A raw count never leaves the installation. Every counter is carried as a bucket label.
    [Fact]
    public async Task ASnapshot_CarriesBucketLabelsRatherThanCounts()
    {
        var builder = Build(new UsageStatisticsCounts(7, 30, 60, 40, 3), "1.2.3");

        var snapshot = await builder.BuildAsync(
            UsageStatisticsTestDoubles.EnabledState(Now),
            UsageStatisticsEdition.Community);

        Assert.Equal("6-20", snapshot.ActiveUsers);
        Assert.Equal("21-100", snapshot.PullRequestsPerWeek);
        Assert.Equal("51-250", snapshot.FindingsRaisedPerWeek);
        Assert.Equal("1-50", snapshot.FindingsAcceptedPerWeek);
        Assert.Equal("1-50", snapshot.FindingsDismissedPerWeek);
    }

    // An installation that records no finding outcomes leaves those two fields out rather than reporting zero.
    // A zero would mean nothing was accepted and would lower the fleet-wide ratio with installations that
    // never measured it.
    [Fact]
    public async Task AnInstallationWithoutOutcomeCollection_OmitsTheOutcomeCounters()
    {
        var builder = Build(new UsageStatisticsCounts(2, 5, 9, null, null), "1.2.3");

        var snapshot = await builder.BuildAsync(
            UsageStatisticsTestDoubles.EnabledState(Now),
            UsageStatisticsEdition.Community);

        Assert.Null(snapshot.FindingsAcceptedPerWeek);
        Assert.Null(snapshot.FindingsDismissedPerWeek);
        Assert.DoesNotContain("findingsAcceptedPerWeek", UsageStatisticsContract.Serialize(snapshot), StringComparison.Ordinal);
    }

    // Throughput is reported per week, so a fortnight of activity is halved.
    [Fact]
    public async Task ALongerWindow_IsNormalisedToOneWeek()
    {
        var state = UsageStatisticsTestDoubles.EnabledState(Now) with { LastSuccessAt = Now.AddDays(-14) };
        var builder = Build(new UsageStatisticsCounts(1, 120, 0, null, null), "1.2.3");

        var snapshot = await builder.BuildAsync(state, UsageStatisticsEdition.Community);

        // 120 over a fortnight is 60 a week. Reported unnormalised it would land a bucket higher.
        Assert.Equal("21-100", snapshot.PullRequestsPerWeek);
    }

    [Fact]
    public async Task TheWindow_StartsAtTheLastDeliveryRatherThanTheLastAttempt()
    {
        DateTimeOffset? capturedStart = null;
        var countSource = Substitute.For<IUsageStatisticsCountSource>();
        countSource
            .CountAsync(Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedStart = callInfo.ArgAt<DateTimeOffset>(0);
                return Task.FromResult(new UsageStatisticsCounts(1, 0, 0, null, null));
            });

        var state = UsageStatisticsTestDoubles.EnabledState(Now) with
        {
            LastSuccessAt = Now.AddDays(-3),
            LastAttemptAt = Now.AddHours(-1),
        };

        var builder = new UsageStatisticsSnapshotBuilder(
            countSource,
            UsageStatisticsTestDoubles.ProductVersion("1.2.3"),
            new FakeTimeProvider(Now));

        await builder.BuildAsync(state, UsageStatisticsEdition.Community);

        Assert.Equal(Now.AddDays(-3), capturedStart);
    }

    // An installation that has never delivered has no window to measure from, so it reports the last week.
    [Fact]
    public void AnInstallationThatHasNeverDelivered_MeasuresTheLastWeek()
    {
        Assert.Equal(TimeSpan.FromDays(7), UsageStatisticsSnapshotBuilder.ResolveWindow(null, Now));
    }

    // Extrapolating three hours of activity to a week multiplies it by 56, which would report a short burst as
    // a sustained workload.
    [Fact]
    public void AVeryShortGap_IsWidenedBeforeARateIsBuiltFromIt()
    {
        Assert.Equal(
            UsageStatisticsSnapshotBuilder.MinimumWindow,
            UsageStatisticsSnapshotBuilder.ResolveWindow(Now.AddHours(-3), Now));
    }

    // Widening a short gap counts the overlapping period twice, so the cadence must never produce a gap
    // shorter than the window floor. The jitter band runs forward from the cadence for this reason.
    [Fact]
    public void TheShortestPossibleCadence_IsNeverShorterThanTheWindowFloor()
    {
        var shortestInterval = UsageStatisticsSendSchedule.NextDelay(Now, Now, 0d);

        Assert.True(shortestInterval >= UsageStatisticsSnapshotBuilder.MinimumWindow);
    }

    [Fact]
    public void ALongDormantInstallation_ReportsRecentActivityRatherThanAYearWideAverage()
    {
        Assert.Equal(
            UsageStatisticsSnapshotBuilder.MaximumWindow,
            UsageStatisticsSnapshotBuilder.ResolveWindow(Now.AddDays(-400), Now));
    }

    private static UsageStatisticsSnapshotBuilder Build(UsageStatisticsCounts counts, string version)
    {
        return new UsageStatisticsSnapshotBuilder(
            UsageStatisticsTestDoubles.CountSource(counts),
            UsageStatisticsTestDoubles.ProductVersion(version),
            new FakeTimeProvider(Now));
    }
}
