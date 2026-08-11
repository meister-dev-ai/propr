// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Resilience;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Tests.Resilience;

/// <summary>
///     Covers the two stages composed the way the runtime composes them, pacing inside retry over one clock the
///     test drives. What a throttled call costs is the question neither stage answers alone: the gate window and
///     the retry backoff are read off the same stated delay, so whether the call that earned the refusal pays that
///     delay once or twice is only visible with both stages in place.
/// </summary>
public sealed class ProviderPacingInsideRetryTests
{
    private const string Connection = "11111111-1111-1111-1111-111111111111";

    private static readonly ProviderCallTarget Target =
        new(AiProviderKind.OpenAiCompatible, "gpt-5.6-luna", "Primary OpenAI");

    /// <summary>The delay the provider states, long enough that paying it twice would be unmistakable.</summary>
    private static readonly TimeSpan Stated = TimeSpan.FromSeconds(7);

    /// <summary>How far the clock moves per step while a call is in flight, which is the resolution measured.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    /// <summary>Three windows, so a call that really waits twice is measured rather than reported as a hang.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(21);

    /// <summary>Real time each step allows the call to react to the clock before it counts as still waiting.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(200);

    /// <summary>Long enough that a hanging test is reported as a failure rather than as a stuck suite.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    // Pacing closes the gate for the delay the provider stated, and retry then waits out that same delay before
    // trying again, which reads like one refusal earning two waits. The second attempt reaches a gate whose window
    // is already up, so the call pays a single window.
    [Fact]
    public async Task AThrottledCallIsDelayedByOneStatedWindowRatherThanTwo()
    {
        var clock = new ManualTimeProvider();
        var gate = Gate(clock);
        var inner = new ScriptedChatClient([Throttle(), null]);

        var call = Client(inner, gate, clock).GetResponseAsync(Hello);
        var delayed = await WaitedOutAsync(clock, call);
        var response = await call.WaitAsync(Patience);

        Assert.Equal("ok", response.Text);
        Assert.Equal(2, inner.Calls);
        Assert.InRange(delayed, Stated, Stated + Tick);
    }

    // The sibling is what the window is for. It never issues the request the provider was always going to refuse,
    // and it starts once the window is up rather than after a refusal and a backoff of its own.
    [Fact]
    public async Task AnotherCallOnTheSameConnectionIsHeldUntilThatWindowElapses()
    {
        var clock = new ManualTimeProvider();
        var gate = Gate(clock);
        var refused = new ScriptedChatClient([Throttle()]);

        // One attempt, so the call that opens the window ends there and leaves the window behind it.
        await Assert.ThrowsAsync<ProviderCallFailedException>(() => Client(refused, gate, clock, attempts: 1).GetResponseAsync(Hello));

        var siblingInner = new ScriptedChatClient([]);
        var sibling = Client(siblingInner, gate, clock).GetResponseAsync(Hello);

        Assert.False(sibling.IsCompleted);
        Assert.Equal(0, siblingInner.Calls);

        clock.Advance(Stated);

        var response = await sibling.WaitAsync(Patience);
        Assert.Equal("ok", response.Text);
        Assert.Equal(1, siblingInner.Calls);
    }

    private static IList<ChatMessage> Hello => [new ChatMessage(ChatRole.User, "hello")];

    /// <summary>The release spread is switched off so a release lands exactly on the window these measure.</summary>
    private static ProviderThrottleGate Gate(TimeProvider clock)
    {
        return new ProviderThrottleGate(clock, TimeSpan.Zero);
    }

    /// <summary>
    ///     Builds the stack the runtime builds: pacing inside retry, one clock behind both, and the policy's
    ///     ceiling serving as the gate's window ceiling, exactly as the decorator wires it.
    /// </summary>
    /// <param name="inner">The client the attempts land on.</param>
    /// <param name="gate">The gate the connection's calls wait on.</param>
    /// <param name="clock">The clock the backoff and the window are both measured against.</param>
    /// <param name="attempts">How many attempts the call is allowed.</param>
    private static ProviderRetryChatClient Client(
        IChatClient inner,
        ProviderThrottleGate gate,
        ManualTimeProvider clock,
        int attempts = 2)
    {
        var policy = new ProviderRetryPolicy
        {
            MaxAttempts = attempts,
            BaseDelay = TimeSpan.FromSeconds(1),
            MaxDelay = TimeSpan.FromSeconds(30),
            JitterFactor = 0,
        };

        var paced = new ProviderPacingChatClient(inner, gate, Connection, Classify, policy.MaxDelay);
        return new ProviderRetryChatClient(paced, policy, Classify, Target, clock);
    }

    /// <summary>Both stages read the same verdict here, as they do in the runtime, where both ask the driver.</summary>
    /// <param name="exception">The failure the attempt threw.</param>
    private static ProviderFailureVerdict Classify(Exception exception)
    {
        return ProviderFailureVerdict.Throttled("the provider throttled the request", Stated, 429);
    }

    /// <summary>
    ///     Moves the clock on a tick at a time until the call finishes, and answers how much clock that took.
    ///     Stepping rather than jumping to an expected instant is what makes the answer a measurement.
    /// </summary>
    /// <param name="clock">The clock both stages wait on.</param>
    /// <param name="call">The call in flight.</param>
    /// <returns>How far the clock had to move before the call completed.</returns>
    private static async Task<TimeSpan> WaitedOutAsync(ManualTimeProvider clock, Task call)
    {
        var advanced = TimeSpan.Zero;
        while (!await SettledAsync(call) && advanced < Limit)
        {
            clock.Advance(Tick);
            advanced += Tick;
        }

        Assert.True(call.IsCompleted, $"The call was still waiting after {advanced.TotalSeconds:0.##}s of clock.");
        return advanced;
    }

    /// <summary>
    ///     Gives the call a moment of real time to react to the clock. Without it, a call that has everything it
    ///     needs reads as still waiting whenever a continuation has yet to run, and the measurement drifts up.
    /// </summary>
    /// <param name="call">The call in flight.</param>
    /// <returns>Whether the call has finished.</returns>
    private static async Task<bool> SettledAsync(Task call)
    {
        try
        {
            await call.WaitAsync(Grace);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static HttpRequestException Throttle()
    {
        return new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests);
    }
}
