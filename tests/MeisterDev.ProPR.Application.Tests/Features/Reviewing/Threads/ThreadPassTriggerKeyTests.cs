// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Threads;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Threads;

/// <summary>
///     The key a thread pass is claimed under. A key that changes for a pull request nobody touched re-queues
///     a pass that already ran, so what the key is built from, and in what order, is load-bearing.
/// </summary>
public sealed class ThreadPassTriggerKeyTests
{
    [Fact]
    public void Build_ThreadIdsOfDifferentDigitLengths_KeepsTheKeyThatWasRecordedWhenTheyWereNumbers()
    {
        // The digests are the ones the numeric key produced for the same logical state. They are pinned
        // literally because the whole point is that an installation's completed passes stay completed: a key
        // rebuilt from unchanged counts must hash to what the database already holds. An ordinal sort would
        // place "10" before "9" and change both.
        Assert.Equal(
            "rev-7|301c86aba64cbf014ed7264d5f967e1a",
            ThreadPassTriggerKey.Build("rev-7", [new("9", 1), new("10", 2)]));

        Assert.Equal(
            "rev-7|de1c7c7427d1335cf2bd819bafbeb063",
            ThreadPassTriggerKey.Build("rev-7", [new("1284003", 5), new("7", 0), new("42", 3)]));
    }

    [Fact]
    public void Build_SameCountsInAnotherOrder_ProducesTheSameKey()
    {
        Assert.Equal(
            ThreadPassTriggerKey.Build("rev-7", [new("9", 1), new("10", 2)]),
            ThreadPassTriggerKey.Build("rev-7", [new("10", 2), new("9", 1)]));
    }

    [Fact]
    public void Build_NonNumericThreadIds_OrdersThemDeterministically()
    {
        var one = ThreadPassTriggerKey.Build(
            "rev-7",
            [new("PRRT_kwDOabc", 1), new("3f2b1c9d", 0), new("PRRT_kwDOabd", 2)]);
        var other = ThreadPassTriggerKey.Build(
            "rev-7",
            [new("PRRT_kwDOabd", 2), new("PRRT_kwDOabc", 1), new("3f2b1c9d", 0)]);

        Assert.Equal(one, other);
    }

    [Fact]
    public void Build_OneMoreComment_ProducesADifferentKey()
    {
        Assert.NotEqual(
            ThreadPassTriggerKey.Build("rev-7", [new("9", 1)]),
            ThreadPassTriggerKey.Build("rev-7", [new("9", 2)]));
    }
}
