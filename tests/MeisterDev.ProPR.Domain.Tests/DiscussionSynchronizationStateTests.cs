// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Domain.Tests;

public sealed class DiscussionSynchronizationStateTests
{
    [Fact]
    public void ConsumerAcknowledgmentsAreIndependentAndVersioned()
    {
        var progress = new DiscussionThreadProgress();
        Assert.True(progress.Needs("archive", "source", "v1"));
        progress.Acknowledge("archive", "source", "v1");
        Assert.False(progress.Needs("archive", "source", "v1"));
        Assert.True(progress.Needs("miss", "source", "v1"));
        Assert.True(progress.Needs("archive", "edited", "v1"));
        Assert.True(progress.Needs("archive", "source", "v2"));
    }

    [Theory]
    [InlineData(0, 15)]
    [InlineData(1, 15)]
    [InlineData(2, 30)]
    [InlineData(3, 60)]
    [InlineData(4, 120)]
    [InlineData(5, 240)]
    [InlineData(6, 480)]
    [InlineData(7, 900)]
    [InlineData(40, 900)]
    [InlineData(int.MaxValue, 900)]
    public void EachFailedAttemptInARowDoublesTheWaitUpToFifteenMinutes(int consecutiveFailures, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), DiscussionSynchronizationState.RetryDelay(consecutiveFailures));
    }
}
