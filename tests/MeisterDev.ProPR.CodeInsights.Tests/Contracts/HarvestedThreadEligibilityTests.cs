using MeisterDev.ProPR.CodeInsights.Contracts;

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.


namespace MeisterDev.ProPR.CodeInsights.Tests.Contracts;

/// <summary>
///     The fallback that keeps ProPR's own words and the provider's own audit entries out of what it is said to
///     have missed, for the two cases identity cannot reach: a summary posted before provenance was recorded, and
///     a row already stored from an earlier harvest.
/// </summary>
public sealed class HarvestedThreadEligibilityTests
{
    [Fact]
    public void ProPrsOwnSummaryIsNotAThreadItFailedToRaise()
    {
        var discussion =
            "0caeb875-08d2-6d69-88fb-302b06d21993: **AI Review Summary**\n\n"
            + "This PR adds a review history experience to the admin UI.";

        Assert.False(HarvestedThreadEligibility.IsHumanThread(discussion));
    }

    [Fact]
    public void ASummaryWithAHumanReplyUnderItIsStillNotAMiss()
    {
        // A person replying to ProPR's summary is talking to ProPR about a review it already gave. Whatever that
        // thread is, it is not something the reviewer failed to raise.
        var discussion =
            "0caeb875-08d2-6d69-88fb-302b06d21993: **AI Review Summary**\n"
            + "alice: thanks, fixed the paging one";

        Assert.False(HarvestedThreadEligibility.IsHumanThread(discussion));
    }

    [Fact]
    public void AThreadOfNothingButProviderActivityIsNotAHumanThread()
    {
        var discussion = "00000002-0000-8888-8000-000000000000: Andreas Rain added Meister ProPR as a reviewer";

        Assert.False(HarvestedThreadEligibility.IsHumanThread(discussion));
    }

    [Fact]
    public void AHumanThreadWithAnActivityEntryOnItStillCounts()
    {
        // The activity entry is not what a person said, and it is not a reason to discard what they did say.
        var discussion =
            "alice: this drops the retry count silently\n"
            + "00000002-0000-8888-8000-000000000000: Andreas Rain added Meister ProPR as a reviewer";

        Assert.True(HarvestedThreadEligibility.IsHumanThread(discussion));
    }

    [Theory]
    [InlineData("alice: this drops the retry count silently")]
    [InlineData("alice@example.com: the guard clause here is inverted")]
    // A comment that happens to talk about a summary is a person talking, not a summary.
    [InlineData("bob: the **AI Review Summary** above missed this file")]
    public void WhatAPersonWroteIsKept(string discussion)
    {
        Assert.True(HarvestedThreadEligibility.IsHumanThread(discussion));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void NothingSaidIsNotAHumanThread(string? discussion)
    {
        Assert.False(HarvestedThreadEligibility.IsHumanThread(discussion));
    }
}
