// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Drivers;

namespace MeisterDev.Ai.Providers.AnthropicAddIn.Tests;

/// <summary>The reasoning levels the Anthropic family reports for a model without a call to the provider.</summary>
public sealed class AnthropicReasoningLevelTests
{
    private static readonly IAiProviderDriver Driver = new AnthropicProviderDriver();

    // A request without a thinking setting turns thinking off, so every Claude model accepts the level none.
    [Fact]
    public void TheCatalogsLevels_GetTheLevelNoneInFront()
    {
        Assert.Equal(
            ["none", "low", "medium", "high", "xhigh", "max"],
            Driver.DescribeReasoningLevels("claude-opus-5", ["low", "medium", "high", "xhigh", "max"]));
        Assert.Equal(["none", "low", "medium", "high"], Driver.DescribeReasoningLevels("claude-haiku-4-5", ["none", "low", "medium", "high"]));
    }

    [Theory]
    [InlineData("claude-opus-5-5")]
    [InlineData("claude-haiku-5-5")]
    [InlineData("claude-fable-5-1")]
    public void AModelFromGeneration55On_TakesTheEffortsTheModelsApiReports(string remoteModelId)
    {
        Assert.Equal(["none", "low", "medium", "high", "xhigh", "max"], Driver.DescribeReasoningLevels(remoteModelId, null));
    }

    [Theory]
    [InlineData("claude-opus-4-7")]
    [InlineData("claude-haiku-4-5")]
    [InlineData("claude-sonnet-4-20250514")]
    public void AnEarlierModelFromGeneration4_TakesLowMediumAndHigh(string remoteModelId)
    {
        Assert.Equal(["none", "low", "medium", "high"], Driver.DescribeReasoningLevels(remoteModelId, null));
    }

    [Fact]
    public void AModelIdOutsideTheClaudeNaming_HasNoLevels()
    {
        Assert.Null(Driver.DescribeReasoningLevels("my-proxy-model", null));
    }
}
