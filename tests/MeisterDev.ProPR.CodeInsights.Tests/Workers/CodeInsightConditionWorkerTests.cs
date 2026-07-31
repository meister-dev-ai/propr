// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.Extensions.Configuration;
using MeisterDev.ProPR.CodeInsights;
using MeisterDev.ProPR.CodeInsights.Workers;

namespace MeisterDev.ProPR.CodeInsights.Tests.Workers;

/// <summary>
///     The thresholds the quality conditions fire at. They are uncalibrated and configurable on purpose, which
///     makes the floors the interesting part: a misconfigured value must not turn every wobble into an alert.
/// </summary>
public sealed class CodeInsightConditionWorkerTests
{
    [Fact]
    public void TheDefaultsAreTheProvisionalOnesDocumentedOnTheWorker()
    {
        var thresholds = CodeInsightConditionWorker.ResolveThresholds(Options());

        Assert.Equal(CodeInsightConditionWorker.DefaultWindowDays, thresholds.WindowDays);
        Assert.Equal(CodeInsightConditionWorker.DefaultCorrectnessDeclineThreshold, thresholds.CorrectnessDeclineThreshold);
        Assert.Equal(CodeInsightConditionWorker.DefaultFalsePositiveShareThreshold, thresholds.FalsePositiveShareThreshold);
        Assert.Equal(CodeInsightConditionWorker.DefaultConcentrationThreshold, thresholds.ConcentrationThreshold);
        Assert.Equal(10, thresholds.MinimumSealedPullRequests);
    }

    [Fact]
    public void ConfiguredThresholdsWin()
    {
        var thresholds = CodeInsightConditionWorker.ResolveThresholds(
            Options(
                ("CODE_INSIGHTS_CONDITION_WINDOW_DAYS", "14"),
                ("CODE_INSIGHTS_F1_DECLINE_THRESHOLD", "0.2"),
                ("CODE_INSIGHTS_FALSE_POSITIVE_SHARE_THRESHOLD", "0.5"),
                ("CODE_INSIGHTS_CONCENTRATION_THRESHOLD", "40"),
                ("CODE_INSIGHTS_MIN_SEALED_PULL_REQUESTS", "25")));

        Assert.Equal(14, thresholds.WindowDays);
        Assert.Equal(0.2, thresholds.CorrectnessDeclineThreshold);
        Assert.Equal(0.5, thresholds.FalsePositiveShareThreshold);
        Assert.Equal(40, thresholds.ConcentrationThreshold);
        Assert.Equal(25, thresholds.MinimumSealedPullRequests);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void AMisconfiguredThresholdIsFlooredRatherThanFiringOnEverything(string value)
    {
        // A zero decline threshold would make every wobble a transition, which is the opposite of an alert. The
        // same reasoning floors the window, the share, the count, and the sample.
        var thresholds = CodeInsightConditionWorker.ResolveThresholds(
            Options(
                ("CODE_INSIGHTS_CONDITION_WINDOW_DAYS", value),
                ("CODE_INSIGHTS_F1_DECLINE_THRESHOLD", value),
                ("CODE_INSIGHTS_FALSE_POSITIVE_SHARE_THRESHOLD", value),
                ("CODE_INSIGHTS_CONCENTRATION_THRESHOLD", value),
                ("CODE_INSIGHTS_MIN_SEALED_PULL_REQUESTS", value)));

        Assert.Equal(1, thresholds.WindowDays);
        Assert.Equal(0.01, thresholds.CorrectnessDeclineThreshold);
        Assert.Equal(0.01, thresholds.FalsePositiveShareThreshold);
        Assert.Equal(1, thresholds.ConcentrationThreshold);
        Assert.Equal(1, thresholds.MinimumSealedPullRequests);
    }

    [Fact]
    public void TheCorrectnessConditionSharesTheViewsSampleFloor()
    {
        // One setting decides both what a view will present as precise and what may raise an alert; two numbers
        // that could disagree would be a way to alert on a metric the view refuses to show.
        var thresholds = CodeInsightConditionWorker.ResolveThresholds(Options(("CODE_INSIGHTS_MIN_SEALED_PULL_REQUESTS", "7")));

        Assert.Equal(7, thresholds.MinimumSealedPullRequests);
    }

    /// <summary>
    ///     The options an installation with these environment keys would end up with, bound through the same
    ///     mapping the host uses, so the keys stay covered now that the worker reads options rather than
    ///     configuration.
    /// </summary>
    private static CodeInsightsOptions Options(params (string Key, string Value)[] settings)
    {
        var options = new CodeInsightsOptions();
        CodeInsightsModuleServiceCollectionExtensions.BindOptions(options, Configuration(settings));
        return options;
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] settings)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(setting => setting.Key, setting => (string?)setting.Value))
            .Build();
    }
}
