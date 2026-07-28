// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Observability;

namespace MeisterDev.ProPR.Observability.Tests;

public sealed class BackgroundActivityScopeTests
{
    [Fact]
    public void IsActive_OutsideAnyScope_IsFalse()
    {
        Assert.False(BackgroundActivityScope.IsActive);
    }

    [Fact]
    public void Begin_MarksTheFlow_AndDisposeClearsIt()
    {
        using (BackgroundActivityScope.Begin())
        {
            Assert.True(BackgroundActivityScope.IsActive);
        }

        Assert.False(BackgroundActivityScope.IsActive);
    }

    /// <summary>A poll cycle that calls another scoped helper must stay suppressed after the inner one ends.</summary>
    [Fact]
    public void Begin_Nested_StaysActiveUntilTheOutermostScopeEnds()
    {
        using (BackgroundActivityScope.Begin())
        {
            using (BackgroundActivityScope.Begin())
            {
                Assert.True(BackgroundActivityScope.IsActive);
            }

            Assert.True(BackgroundActivityScope.IsActive);
        }

        Assert.False(BackgroundActivityScope.IsActive);
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotLeakSuppressionOutOfTheScope()
    {
        var outer = BackgroundActivityScope.Begin();
        var inner = BackgroundActivityScope.Begin();

        inner.Dispose();
        inner.Dispose();

        Assert.True(BackgroundActivityScope.IsActive);

        outer.Dispose();

        Assert.False(BackgroundActivityScope.IsActive);
    }

    /// <summary>
    ///     The whole point is that the flag reaches an outbound request several awaits deep, since that is
    ///     where the instrumentation filter reads it.
    /// </summary>
    [Fact]
    public async Task IsActive_FlowsIntoAwaitedWork()
    {
        static async Task<bool> ReadAfterAwaitsAsync()
        {
            await Task.Yield();
            await Task.Delay(1);
            return BackgroundActivityScope.IsActive;
        }

        using (BackgroundActivityScope.Begin())
        {
            Assert.True(await ReadAfterAwaitsAsync());
        }

        Assert.False(await ReadAfterAwaitsAsync());
    }

    /// <summary>
    ///     Foreground request handling runs concurrently with the pollers, so a scope entered on one flow
    ///     must not suppress spans on another.
    /// </summary>
    [Fact]
    public async Task IsActive_DoesNotLeakAcrossConcurrentFlows()
    {
        var backgroundEntered = new TaskCompletionSource();
        var foregroundObserved = new TaskCompletionSource<bool>();

        var background = Task.Run(async () =>
        {
            using (BackgroundActivityScope.Begin())
            {
                backgroundEntered.SetResult();
                await foregroundObserved.Task;
            }
        });

        var foreground = Task.Run(async () =>
        {
            await backgroundEntered.Task;
            foregroundObserved.SetResult(BackgroundActivityScope.IsActive);
        });

        await Task.WhenAll(background, foreground);

        Assert.False(await foregroundObserved.Task);
    }
}
