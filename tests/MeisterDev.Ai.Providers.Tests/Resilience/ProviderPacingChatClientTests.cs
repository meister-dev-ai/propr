// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using MeisterDev.Ai.Providers.Resilience;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Tests.Resilience;

/// <summary>
///     Covers what a fan-out of review calls gets out of pacing. One refusal is enough to hold the siblings back,
///     while anything that is not a throttle leaves them alone. The failure itself always reaches the retry stage,
///     whose business it is.
/// </summary>
public sealed class ProviderPacingChatClientTests
{
    private const string Connection = "11111111-1111-1111-1111-111111111111";

    private static readonly TimeSpan Stated = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    /// <summary>Long enough that a test hanging is reported as a failure rather than as a stuck suite.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task AThrottleClosesTheGateForAsLongAsTheProviderAsked()
    {
        var clock = new ManualTimeProvider();
        var gate = Gate(clock);
        var inner = new ScriptedChatClient([Throttle()]);

        await Assert.ThrowsAsync<HttpRequestException>(() => Client(inner, gate, ThrottledFor(Stated)).GetResponseAsync(Hello));

        var held = gate.WaitAsync(Connection).AsTask();
        Assert.False(held.IsCompleted);

        clock.Advance(Stated);
        await held.WaitAsync(Patience);
    }

    // The behaviour the whole stage exists for: the sibling never issues a request the provider was always going
    // to refuse, and it starts as soon as the window is up rather than after a backoff of its own.
    [Fact]
    public async Task ASiblingWaitsRatherThanIssuingItsOwnThrottledRequest()
    {
        var clock = new ManualTimeProvider();
        var gate = Gate(clock);
        var throttled = new ScriptedChatClient([Throttle()]);
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(throttled, gate, ThrottledFor(Stated)).GetResponseAsync(Hello));

        var siblingInner = new ScriptedChatClient([]);
        var sibling = Client(siblingInner, gate, ThrottledFor(Stated)).GetResponseAsync(Hello);

        Assert.False(sibling.IsCompleted);
        Assert.Equal(0, siblingInner.Calls);

        clock.Advance(Stated);

        var response = await sibling.WaitAsync(Patience);
        Assert.Equal("ok", response.Text);
        Assert.Equal(1, siblingInner.Calls);
    }

    // A call to a different connection has its own quota, so a throttle over here must not stop it.
    [Fact]
    public async Task ACallToAnotherConnectionIsUnaffected()
    {
        var clock = new ManualTimeProvider();
        var gate = Gate(clock);
        var throttled = new ScriptedChatClient([Throttle()]);
        await Assert.ThrowsAsync<HttpRequestException>(() => Client(throttled, gate, ThrottledFor(Stated)).GetResponseAsync(Hello));

        var elsewhere = new ScriptedChatClient([]);
        var client = new ProviderPacingChatClient(
            elsewhere,
            gate,
            "22222222-2222-2222-2222-222222222222",
            ThrottledFor(Stated),
            Ceiling);

        var response = await client.GetResponseAsync(Hello).WaitAsync(Patience);

        Assert.Equal("ok", response.Text);
    }

    [Fact]
    public async Task AFailureThatIsNotAThrottleLeavesTheGateOpen()
    {
        var gate = Gate(new ManualTimeProvider());
        var inner = new ScriptedChatClient([new HttpRequestException("server error", null, HttpStatusCode.BadGateway)]);
        var client = Client(inner, gate, _ => ProviderFailureVerdict.Transient("a server error", Stated, 502));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync(Hello));

        var wait = gate.WaitAsync(Connection);
        Assert.True(wait.IsCompleted);
        await wait;
    }

    // Holding an entire fan-out on a number nobody stated would cost more than the throttle does, so a throttle
    // that says nothing about timing is left to the retry stage's own schedule.
    [Fact]
    public async Task AThrottleThatStatesNoDelayLeavesTheGateOpen()
    {
        var gate = Gate(new ManualTimeProvider());
        var inner = new ScriptedChatClient([Throttle()]);
        var client = Client(inner, gate, _ => ProviderFailureVerdict.Throttled("throttled", null, 429));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync(Hello));

        var wait = gate.WaitAsync(Connection);
        Assert.True(wait.IsCompleted);
        await wait;
    }

    // Deciding what to do about the failure belongs to retry, so the exception has to arrive there as the same
    // object that came off the wire.
    [Fact]
    public async Task TheFailureIsPassedOnExactlyAsItArrived()
    {
        var gate = Gate(new ManualTimeProvider());
        var failure = Throttle();
        var inner = new ScriptedChatClient([failure]);

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() => Client(inner, gate, ThrottledFor(Stated)).GetResponseAsync(Hello));

        Assert.Same(failure, thrown);
    }

    // A provider asking for an hour is still telling the truth about its quota, but a review cannot be parked
    // for an hour, so the policy's ceiling decides how long the gate stays shut.
    [Fact]
    public async Task AnAbsurdStatedDelayIsCappedBeforeTheGateCloses()
    {
        var clock = new ManualTimeProvider();
        var gate = Gate(clock);
        var inner = new ScriptedChatClient([Throttle()]);

        await Assert.ThrowsAsync<HttpRequestException>(() => Client(inner, gate, ThrottledFor(TimeSpan.FromHours(1))).GetResponseAsync(Hello));

        // Checked at each step rather than only at the end: a gate that was never closed at all would release
        // the waiter immediately and pass an assertion that only looks after the clock has moved.
        var held = gate.WaitAsync(Connection).AsTask();
        Assert.False(held.IsCompleted);

        clock.Advance(Ceiling - TimeSpan.FromSeconds(1));
        Assert.False(held.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(1));
        await held.WaitAsync(Patience);
    }

    // A stopped review has to stop while it is waiting on a gate too, and it must not take the throttle everyone
    // else is respecting with it.
    [Fact]
    public async Task CancellationWhileGatedPropagatesAtOnceAndIssuesNoRequest()
    {
        var gate = Gate(new ManualTimeProvider());
        gate.CloseFor(Connection, TimeSpan.FromSeconds(30));
        var inner = new ScriptedChatClient([]);
        using var cancellation = new CancellationTokenSource();

        var call = Client(inner, gate, ThrottledFor(Stated)).GetResponseAsync(Hello, cancellationToken: cancellation.Token);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Patience));
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task AThrottleOnTheStreamingPathClosesTheGateToo()
    {
        var clock = new ManualTimeProvider();
        var gate = Gate(clock);
        var inner = new ScriptedChatClient([Throttle()]);
        var client = Client(inner, gate, ThrottledFor(Stated));

        var updates = new List<string>();
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync(Hello))
            {
                updates.Add(update.Text);
            }
        });

        Assert.Empty(updates);
        var held = gate.WaitAsync(Connection).AsTask();
        Assert.False(held.IsCompleted);

        clock.Advance(Stated);
        await held.WaitAsync(Patience);
    }

    [Fact]
    public async Task AStreamedAnswerWaitsOutAClosedGateBeforeItStarts()
    {
        var clock = new ManualTimeProvider();
        var gate = Gate(clock);
        gate.CloseFor(Connection, Stated);
        var inner = new ScriptedChatClient([]);
        var client = Client(inner, gate, ThrottledFor(Stated));

        // Driven a step at a time rather than with await foreach, so the gate wait is observable while it is
        // still unfinished; the clock only moves once the waiter is genuinely there.
        var stream = client.GetStreamingResponseAsync(Hello).GetAsyncEnumerator();
        var first = stream.MoveNextAsync();
        Assert.False(first.IsCompleted);
        Assert.Equal(0, inner.Calls);

        clock.Advance(Stated);

        Assert.True(await first.AsTask().WaitAsync(Patience));
        Assert.Equal("ok", stream.Current.Text);
        await stream.DisposeAsync();
    }

    // A stream can be refused part way through, after the caller has already read updates off it. The quota is
    // exhausted whatever point the answer had reached, so the gate has to close on that too.
    [Fact]
    public async Task AThrottleArrivingMidStreamStillClosesTheGate()
    {
        var clock = new ManualTimeProvider();
        var gate = Gate(clock);
        var inner = new ScriptedChatClient([], failAfterFirstUpdate: true);
        var client = Client(inner, gate, ThrottledFor(Stated));

        var updates = new List<string>();
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync(Hello))
            {
                updates.Add(update.Text);
            }
        });

        Assert.Equal(["ok"], updates);
        var held = gate.WaitAsync(Connection).AsTask();
        Assert.False(held.IsCompleted);

        clock.Advance(Stated);
        await held.WaitAsync(Patience);
    }

    // Classification is the driver's own code, and pacing is an optimisation over what retry does anyway. A
    // classifier that blows up may cost the gate closure; it must not cost the failure retry has to judge.
    [Fact]
    public async Task AClassifierThatThrowsLeavesTheOriginalFailureOnItsWayUp()
    {
        var gate = Gate(new ManualTimeProvider());
        var failure = Throttle();
        var inner = new ScriptedChatClient([failure]);
        var client = Client(inner, gate, _ => throw new InvalidOperationException("the classifier is broken"));

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync(Hello));

        Assert.Same(failure, thrown);
        var wait = gate.WaitAsync(Connection);
        Assert.True(wait.IsCompleted);
        await wait;
    }

    // A stream that fails the read and then fails its own cleanup. Letting the cleanup win would hand the retry
    // stage an error that is not a throttle, so the call it should have retried is given up on instead.
    [Fact]
    public async Task ADisposalFailureDoesNotReplaceTheThrottleItFollows()
    {
        var clock = new ManualTimeProvider();
        var gate = Gate(clock);
        var failure = Throttle();
        var inner = new FailingStreamChatClient(failure, new InvalidOperationException("the stream would not close"));
        var client = Client(inner, gate, ThrottledFor(Stated));

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var unused in client.GetStreamingResponseAsync(Hello))
            {
            }
        });

        Assert.Same(failure, thrown);

        // Noting the throttle happens before the cleanup runs, so the window is in force either way.
        var held = gate.WaitAsync(Connection).AsTask();
        Assert.False(held.IsCompleted);
        clock.Advance(Stated);
        await held.WaitAsync(Patience);
    }

    private static IList<ChatMessage> Hello => [new ChatMessage(ChatRole.User, "hello")];

    /// <summary>The jitter is switched off so a release lands exactly on the window, which is what these assert.</summary>
    private static ProviderThrottleGate Gate(TimeProvider clock)
    {
        return new ProviderThrottleGate(clock, TimeSpan.Zero);
    }

    private static ProviderPacingChatClient Client(
        IChatClient inner,
        ProviderThrottleGate gate,
        Func<Exception, ProviderFailureVerdict> classify)
    {
        return new ProviderPacingChatClient(inner, gate, Connection, classify, Ceiling);
    }

    private static Func<Exception, ProviderFailureVerdict> ThrottledFor(TimeSpan stated)
    {
        return _ => ProviderFailureVerdict.Throttled("the provider throttled the request", stated, 429);
    }

    private static HttpRequestException Throttle()
    {
        return new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests);
    }

    /// <summary>
    ///     Serves a stream that refuses the read and then refuses to close. Hand-rolled rather than scripted,
    ///     because a compiler-generated iterator cannot fail its own disposal on demand.
    /// </summary>
    /// <param name="readFailure">What the read throws.</param>
    /// <param name="disposeFailure">What the cleanup throws afterwards.</param>
    private sealed class FailingStreamChatClient(Exception readFailure, Exception disposeFailure) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("This client serves the streaming path only.");
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return new FailingStream(readFailure, disposeFailure);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public TService? GetService<TService>(object? key = null)
            where TService : class => null;

        public void Dispose()
        {
        }

        private sealed class FailingStream(Exception readFailure, Exception disposeFailure)
            : IAsyncEnumerable<ChatResponseUpdate>, IAsyncEnumerator<ChatResponseUpdate>
        {
            public ChatResponseUpdate Current => throw new NotSupportedException("This stream yields nothing.");

            public IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            {
                return this;
            }

            public ValueTask<bool> MoveNextAsync() => throw readFailure;

            public ValueTask DisposeAsync() => throw disposeFailure;
        }
    }
}
