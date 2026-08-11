// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Resilience;

namespace MeisterDev.Ai.Providers.Tests.Resilience;

/// <summary>
///     Covers the primitive one throttled call uses to hold back the rest: it must let calls through when
///     nothing is throttled, release them exactly when the stated window is up, keep one connection's trouble
///     away from another's, and leave the caller's cancellation alone.
/// </summary>
public sealed class ProviderThrottleGateTests
{
    private const string Connection = "connection-a";
    private const string OtherConnection = "connection-b";

    /// <summary>Long enough that a test hanging is reported as a failure rather than as a stuck suite.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task AnOpenGateCostsTheCallerNothing()
    {
        var gate = Gate(new ManualTimeProvider());

        var wait = gate.WaitAsync(Connection);

        Assert.True(wait.IsCompleted);
        await wait;
    }

    [Fact]
    public async Task AWaiterIsReleasedWhenTheWindowElapses()
    {
        var clock = new ManualTimeProvider();
        var gate = Gate(clock);
        gate.CloseFor(Connection, TimeSpan.FromSeconds(4));

        var wait = gate.WaitAsync(Connection).AsTask();
        Assert.False(wait.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(4));

        await wait.WaitAsync(Patience);
    }

    [Fact]
    public async Task AWaiterIsStillHeldPartWayThroughTheWindow()
    {
        var clock = new ManualTimeProvider();
        var gate = Gate(clock);
        gate.CloseFor(Connection, TimeSpan.FromSeconds(4));

        var wait = gate.WaitAsync(Connection).AsTask();
        clock.Advance(TimeSpan.FromSeconds(3));

        Assert.False(wait.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(1));
        await wait.WaitAsync(Patience);
    }

    // One provider account running out of quota says nothing about another, so a closed gate must not be a
    // process-wide stop on model calls.
    [Fact]
    public async Task AGateClosedForOneConnectionLeavesTheOthersFree()
    {
        var gate = Gate(new ManualTimeProvider());
        gate.CloseFor(Connection, TimeSpan.FromSeconds(30));

        var wait = gate.WaitAsync(OtherConnection);

        Assert.True(wait.IsCompleted);
        await wait;
    }

    // A second refusal while the first window is still running means the quota has not recovered, so the later
    // instant wins. The reverse would let a short window cut a long one short and send everyone back in early.
    [Fact]
    public async Task TheLaterOfTwoWindowsIsTheOneThatIsWaitedOut()
    {
        var clock = new ManualTimeProvider();
        var gate = Gate(clock);
        gate.CloseFor(Connection, TimeSpan.FromSeconds(10));
        gate.CloseFor(Connection, TimeSpan.FromSeconds(2));

        var wait = gate.WaitAsync(Connection).AsTask();
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(wait.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(8));
        await wait.WaitAsync(Patience);
    }

    // A cancelled review has to stop now, not when some provider's quota happens to recover. And the throttle
    // it was waiting on is still true for everyone else, so giving up must not reopen the gate.
    [Fact]
    public async Task ACancelledWaiterGivesUpAtOnceAndLeavesTheGateClosed()
    {
        var clock = new ManualTimeProvider();
        var gate = Gate(clock);
        gate.CloseFor(Connection, TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();

        var abandoned = gate.WaitAsync(Connection, cancellation.Token).AsTask();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned.WaitAsync(Patience));

        var stillHeld = gate.WaitAsync(Connection).AsTask();
        Assert.False(stillHeld.IsCompleted);
    }

    [Fact]
    public async Task AWindowThatHasAlreadyElapsedHoldsNobody()
    {
        var clock = new ManualTimeProvider();
        var gate = Gate(clock);
        gate.CloseFor(Connection, TimeSpan.FromSeconds(4));
        clock.Advance(TimeSpan.FromSeconds(5));

        var wait = gate.WaitAsync(Connection);

        Assert.True(wait.IsCompleted);
        await wait;
    }

    [Fact]
    public async Task ClosingForNoTimeAtAllChangesNothing()
    {
        var gate = Gate(new ManualTimeProvider());

        gate.CloseFor(Connection, TimeSpan.Zero);

        var wait = gate.WaitAsync(Connection);
        Assert.True(wait.IsCompleted);
        await wait;
    }

    // A single waiter would prove nothing about the case the gate is for. A fan-out puts several callers on one
    // key at once, and every one of them has to be held and then let through.
    [Fact]
    public async Task EveryWaiterOnOneConnectionIsHeldAndThenReleased()
    {
        var clock = new ManualTimeProvider();
        var gate = Gate(clock);
        gate.CloseFor(Connection, TimeSpan.FromSeconds(4));

        var waiters = Enumerable.Range(0, 8).Select(_ => gate.WaitAsync(Connection).AsTask()).ToArray();
        Assert.All(waiters, waiter => Assert.False(waiter.IsCompleted));

        clock.Advance(TimeSpan.FromSeconds(4));

        await Task.WhenAll(waiters).WaitAsync(Patience);
    }

    // A stated delay of a week is a provider talking nonsense, and the deadline arithmetic and Task.Delay both
    // have limits well inside it, so the window is clamped rather than allowed to throw out of CloseFor.
    [Fact]
    public async Task AnUnboundedWindowIsClampedRatherThanThrowing()
    {
        var clock = new ManualTimeProvider();
        var gate = Gate(clock);

        gate.CloseFor(Connection, TimeSpan.MaxValue);

        var wait = gate.WaitAsync(Connection).AsTask();
        Assert.False(wait.IsCompleted);

        clock.Advance(TimeSpan.FromHours(1));
        await wait.WaitAsync(Patience);
    }

    /// <summary>The jitter is switched off so a release lands exactly on the window, which is what these assert.</summary>
    private static ProviderThrottleGate Gate(TimeProvider clock)
    {
        return new ProviderThrottleGate(clock, TimeSpan.Zero);
    }
}
