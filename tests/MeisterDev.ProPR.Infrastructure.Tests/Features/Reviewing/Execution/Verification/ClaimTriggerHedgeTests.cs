// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution.Verification;

public sealed class ClaimTriggerHedgeTests
{
    [Theory]
    [InlineData(
        "The method dereferences the key. Whether callers can pass null is not shown in this diff.", "Whether callers can pass null is not shown in this diff")]
    [InlineData("The flag is set on every failure. The diff does not show how it is cleared.", "The diff does not show how it is cleared")]
    [InlineData("The earlier load can overwrite the result. This depends on whether loads can overlap.", "This depends on whether loads can overlap")]
    [InlineData(
        "The intended precedence among claim outcomes is not evident in the diff.", "The intended precedence among claim outcomes is not evident in the diff")]
    [InlineData("The verdict's semantics are not defined in this file.", "The verdict's semantics are not defined in this file")]
    [InlineData(
        "The value reaches Claim.cs unchecked. Whether Claim.cs validates it is not shown in this diff.",
        "Whether Claim.cs validates it is not shown in this diff")]
    public void FindOpenCondition_ReturnsTheSentenceThatLeavesTheTriggerOpen(string claim, string expected)
    {
        Assert.Equal(expected, ClaimTriggerHedge.FindOpenCondition(claim));
    }

    [Theory]
    [InlineData("The label does not show the severity, so readers mistake it for the type.")]
    [InlineData("The method returns null when the key is missing, and the caller dereferences the result.")]
    [InlineData("")]
    [InlineData(null)]
    public void FindOpenCondition_IgnoresClaimsThatStateTheirTrigger(string? claim)
    {
        Assert.Null(ClaimTriggerHedge.FindOpenCondition(claim));
    }

    [Fact]
    public void FindOpenCondition_BoundsALongSentence()
    {
        var claim = "Whether " + new string('x', 300) + " is not established.";

        var condition = ClaimTriggerHedge.FindOpenCondition(claim);

        Assert.NotNull(condition);
        Assert.True(condition.Length <= 121);
        Assert.EndsWith("…", condition);
    }
}
