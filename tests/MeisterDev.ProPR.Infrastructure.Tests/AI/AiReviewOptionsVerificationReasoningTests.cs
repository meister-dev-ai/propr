// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Infrastructure.AI;
using Microsoft.Extensions.Configuration;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

public sealed class AiReviewOptionsVerificationReasoningTests
{
    // Any level name a provider uses is read, none included; a value that is not a level name leaves the judge on its
    // own defaults.
    [Theory]
    [InlineData("medium", "medium")]
    [InlineData("High", "high")]
    [InlineData("none", "none")]
    [InlineData("max", "max")]
    [InlineData("9", null)]
    [InlineData("two words", null)]
    [InlineData(null, null)]
    public void Bind_ReadsTheVerificationReasoningLevelAndKeepsTheDefaultForAValueThatIsNotALevelName(string? value, string? expected)
    {
        Assert.Equal(expected, Bind(new Dictionary<string, string?> { ["AI_VERIFICATION_REASONING_LEVEL"] = value }));
    }

    // The former setting documented none as its default, meaning that the judge and the checks keep their own defaults.
    // An installation that wrote it out keeps the judge at low.
    [Theory]
    [InlineData("none", null)]
    [InlineData("None", null)]
    [InlineData("high", "high")]
    [InlineData("xhigh", "xhigh")]
    public void Bind_ReadsTheFormerSetting_WithNoneMeaningNotSet(string value, string? expected)
    {
        Assert.Equal(expected, Bind(new Dictionary<string, string?> { ["AI_VERIFICATION_REASONING_EFFORT"] = value }));
    }

    [Fact]
    public void Bind_PrefersTheLevelSetting_OverTheFormerSetting()
    {
        var level = Bind(
            new Dictionary<string, string?>
            {
                ["AI_VERIFICATION_REASONING_LEVEL"] = "none",
                ["AI_VERIFICATION_REASONING_EFFORT"] = "high",
            });

        Assert.Equal("none", level);
    }

    private static string? Bind(Dictionary<string, string?> values)
    {
        var options = new AiReviewOptions();
        AiReviewOptionsBinder.Bind(options, new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return options.VerificationReasoningLevel;
    }
}
