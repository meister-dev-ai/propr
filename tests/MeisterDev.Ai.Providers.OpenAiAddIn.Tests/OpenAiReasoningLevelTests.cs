// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Drivers;

namespace MeisterDev.Ai.Providers.OpenAiAddIn.Tests;

/// <summary>The reasoning levels the OpenAI family reports for a model without a call to the provider.</summary>
public sealed class OpenAiReasoningLevelTests
{
    private static readonly IAiProviderDriver Driver = new OpenAiProviderDriver();

    [Fact]
    public void TheCatalogsLevels_ApplyToAModelTheCatalogLists()
    {
        Assert.Equal(["low", "medium", "high"], Driver.DescribeReasoningLevels("gpt-6.1-terra", ["low", "medium", "high"]));
    }

    [Theory]
    [InlineData("gpt-6-luna", new[] { "none", "low", "medium", "high", "xhigh", "max" })]
    [InlineData("gpt-6.1-terra", new[] { "none", "low", "medium", "high", "xhigh", "max" })]
    [InlineData("gpt-6.1-sol", new[] { "low", "medium", "high", "xhigh", "max" })]
    [InlineData("gpt-6.1-terra-2026-10-01", new[] { "none", "low", "medium", "high", "xhigh", "max" })]
    public void TheGpt6Models_HaveTheLevelsTheResponsesApiAccepts(string remoteModelId, string[] expected)
    {
        Assert.Equal(expected, Driver.DescribeReasoningLevels(remoteModelId, null));
    }

    [Theory]
    [InlineData("gpt-6.1-terra-mini")]
    [InlineData("gpt-4o")]
    public void AModelTheFamilyHasNoValuesFor_HasNoLevels(string remoteModelId)
    {
        Assert.Null(Driver.DescribeReasoningLevels(remoteModelId, null));
    }
}
