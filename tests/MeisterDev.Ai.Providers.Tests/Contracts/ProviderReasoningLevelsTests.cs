// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;

namespace MeisterDev.Ai.Providers.Tests.Contracts;

public sealed class ProviderReasoningLevelsTests
{
    [Fact]
    public void Normalize_TrimsLowercasesAndDropsRepeatedNames_AndSortsSharedNamesByEffort()
    {
        var levels = ProviderReasoningLevels.Normalize([" MAX", "low", "None", "low ", "xhigh"]);

        Assert.Equal(["none", "low", "xhigh", "max"], levels);
    }

    // The position of a name the shared order does not hold cannot be inferred, so such a list keeps its source order.
    [Fact]
    public void Normalize_KeepsTheSourceOrder_WhenAListHoldsANameOutsideTheSharedOrder()
    {
        var levels = ProviderReasoningLevels.Normalize(["high", "low", "ultra"]);

        Assert.Equal(["high", "low", "ultra"], levels);
    }

    [Fact]
    public void Normalize_DropsNamesThatAreNotLevelNames()
    {
        var levels = ProviderReasoningLevels.Normalize(["", null, "two words", "1high", "x-high_2", new string('a', 33)]);

        Assert.Equal(["x-high_2"], levels);
    }

    [Fact]
    public void Normalize_KeepsAtMostTheLargestNumberOfLevels()
    {
        var levels = ProviderReasoningLevels.Normalize(Enumerable.Range(0, 20).Select(index => $"level{index}"));

        Assert.Equal(ProviderReasoningLevels.MaxCount, levels!.Count);
    }

    [Fact]
    public void Normalize_ReturnsNull_ForNull_AndAnEmptyList_ForAnEmptyList()
    {
        Assert.Null(ProviderReasoningLevels.Normalize(null));
        Assert.Empty(ProviderReasoningLevels.Normalize([])!);
    }

    [Theory]
    [InlineData("none", 0)]
    [InlineData("medium", 3)]
    [InlineData("max", 6)]
    [InlineData("ultra", -1)]
    [InlineData(null, -1)]
    public void RankOf_IsThePositionInTheSharedOrder(string? name, int expected)
    {
        Assert.Equal(expected, ProviderReasoningLevels.RankOf(name));
    }
}
