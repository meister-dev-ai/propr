// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Services;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Tests.Features.Reviewing.Execution;

/// <summary>
///     The routing-tier join is a commutative Max over the FileComplexityTier lattice
///     with floors as absolute levels (never increments).
/// </summary>
public sealed class TierJoinTests
{
    [Fact]
    public void Max_ReturnsHighestTier()
    {
        Assert.Equal(FileComplexityTier.High, TierJoin.Max(FileComplexityTier.Low, FileComplexityTier.High, FileComplexityTier.Medium));
        Assert.Equal(FileComplexityTier.Medium, TierJoin.Max(FileComplexityTier.Low, FileComplexityTier.Medium));
        Assert.Equal(FileComplexityTier.Low, TierJoin.Max(FileComplexityTier.Low));
    }

    [Fact]
    public void Max_NoOperands_IsLow()
    {
        Assert.Equal(FileComplexityTier.Low, TierJoin.Max());
    }

    [Fact]
    public void Max_IsCommutative()
    {
        Assert.Equal(
            TierJoin.Max(FileComplexityTier.Low, FileComplexityTier.High),
            TierJoin.Max(FileComplexityTier.High, FileComplexityTier.Low));
        Assert.Equal(
            TierJoin.Max(FileComplexityTier.Medium, FileComplexityTier.High, FileComplexityTier.Low),
            TierJoin.Max(FileComplexityTier.High, FileComplexityTier.Low, FileComplexityTier.Medium));
    }

    [Theory]
    [InlineData(FanOutKind.Truncated, FileComplexityTier.Medium)]
    [InlineData(FanOutKind.Measured, FileComplexityTier.Low)]
    [InlineData(FanOutKind.Unavailable, FileComplexityTier.Low)]
    public void FloorFromFanOut_TruncatedFloorsAtMedium_OthersContributeNoFloor(FanOutKind kind, FileComplexityTier expected)
    {
        var signal = kind switch
        {
            FanOutKind.Truncated => FanOutSignal.Truncated(99),
            FanOutKind.Measured => FanOutSignal.Measured(3),
            _ => FanOutSignal.Unavailable,
        };

        Assert.Equal(expected, TierJoin.FloorFromFanOut(signal));
    }

    [Fact]
    public void Floor_IsAbsoluteLevel_NotIncrement()
    {
        var floor = TierJoin.FloorFromFanOut(FanOutSignal.Truncated(50));

        // Raises Low -> Medium, but does NOT bump Medium -> High or High -> beyond: a level, not a +1 increment.
        Assert.Equal(FileComplexityTier.Medium, TierJoin.Max(FileComplexityTier.Low, floor));
        Assert.Equal(FileComplexityTier.Medium, TierJoin.Max(FileComplexityTier.Medium, floor));
        Assert.Equal(FileComplexityTier.High, TierJoin.Max(FileComplexityTier.High, floor));
    }
}
