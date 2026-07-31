// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.CodeInsights.Dispositions;

namespace MeisterDev.ProPR.CodeInsights.Tests.Dispositions;

/// <summary>
///     The deterministic half of a finding's outcome. It is separated from the model call precisely so this
///     part is reproducible from stored inputs, which is what makes the metrics recomputable.
/// </summary>
public sealed class FindingDispositionMapperTests
{
    [Theory]
    [InlineData(ThreadAnchorCodeChange.Changed)]
    [InlineData(ThreadAnchorCodeChange.Unchanged)]
    [InlineData(ThreadAnchorCodeChange.Unknown)]
    public void AHumanAcceptingTheConcern_IsAcknowledgedWithOrWithoutACodeChange(ThreadAnchorCodeChange change)
    {
        // "By design" and "won't fix" are agreement, not neglect: the acceptance is itself the outcome and
        // needs nothing to corroborate it.
        var result = FindingDispositionMapper.MapFromSignals(ThreadResolutionIntent.AcceptedByHuman, change);

        Assert.Equal(CodeInsightDisposition.Acknowledged, result);
    }

    [Fact]
    public void AClaimedFixBackedByACodeChange_IsAddressed()
    {
        var result = FindingDispositionMapper.MapFromSignals(
            ThreadResolutionIntent.ClaimsFix,
            ThreadAnchorCodeChange.Changed);

        Assert.Equal(CodeInsightDisposition.Addressed, result);
    }

    [Theory]
    [InlineData(ThreadAnchorCodeChange.Unchanged)]
    [InlineData(ThreadAnchorCodeChange.Unknown)]
    public void AClaimedFixWithNoCorroboratingChange_IsLeftToJudgement(ThreadAnchorCodeChange change)
    {
        // Treating a claim as a fix would inflate the one number the whole feature exists to report honestly.
        var result = FindingDispositionMapper.MapFromSignals(ThreadResolutionIntent.ClaimsFix, change);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(ThreadAnchorCodeChange.Changed)]
    [InlineData(ThreadAnchorCodeChange.Unchanged)]
    [InlineData(ThreadAnchorCodeChange.Unknown)]
    public void AThreadWithNoDiscernibleResolution_IsLeftToJudgement(ThreadAnchorCodeChange change)
    {
        // Whether the finding was wrong or simply unwanted cannot be read from a status, which is the entire
        // reason that split is judged from the discussion instead.
        var result = FindingDispositionMapper.MapFromSignals(ThreadResolutionIntent.Active, change);

        Assert.Null(result);
    }

    [Fact]
    public void EverySignalCombination_IsAccountedFor()
    {
        // A new intent or change value must not fall through this mapping unnoticed: it would silently start
        // routing outcomes to the judgement path, or worse, to the wrong bucket.
        foreach (var intent in Enum.GetValues<ThreadResolutionIntent>())
        {
            foreach (var change in Enum.GetValues<ThreadAnchorCodeChange>())
            {
                var result = FindingDispositionMapper.MapFromSignals(intent, change);
                if (result is not null)
                {
                    Assert.True(
                        result is CodeInsightDisposition.Addressed or CodeInsightDisposition.Acknowledged,
                        $"{intent}/{change} produced {result}, which the signals alone cannot establish.");
                }
            }
        }
    }
}
