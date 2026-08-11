// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Resilience;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Tests.Resilience;

/// <summary>
///     Covers what a review depends on when a provider misbehaves: a throttled call is repeated rather than
///     abandoned, a rejected one fails immediately with a cause an operator can act on, and cancellation is never
///     turned into either.
/// </summary>
public sealed class ProviderRetryChatClientTests
{
    private static readonly ProviderCallTarget Target =
        new(AiProviderKind.OpenAiCompatible, "deepseek-reasoner", "Primary DeepSeek");

    private static readonly ProviderRetryPolicy Immediate = new()
    {
        MaxAttempts = 3,
        BaseDelay = TimeSpan.Zero,
        MaxDelay = TimeSpan.FromSeconds(30),
        JitterFactor = 0,
    };

    [Fact]
    public async Task ATransientFailureIsRepeatedAndTheAnswerStillArrives()
    {
        var inner = new ScriptedChatClient([Transient(), null]);

        var response = await Client(inner).GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.Equal("ok", response.Text);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task APermanentFailureIsNotRepeated()
    {
        var inner = new ScriptedChatClient([Permanent()]);

        await Assert.ThrowsAsync<ProviderCallFailedException>(() => Client(inner).GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task ExhaustingTheAttemptsReportsHowManyWereSpentAndKeepsTheProviderError()
    {
        var inner = new ScriptedChatClient([Transient(), Transient(), Transient()]);

        var failure = await Assert.ThrowsAsync<ProviderCallFailedException>(() => Client(inner).GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        Assert.Equal(3, inner.Calls);
        Assert.Equal(3, failure.Attempts);
        Assert.True(failure.Verdict.IsTransient);
        Assert.IsType<HttpRequestException>(failure.InnerException);
    }

    // The message is what lands on a failed job as its recorded cause, so it has to name the profile an operator
    // would go and look at, and say something about what to do next.
    [Fact]
    public async Task TheFailureNamesTheProfileTheModelAndWhatToTryNext()
    {
        var inner = new ScriptedChatClient([Permanent(401)]);

        var failure = await Assert.ThrowsAsync<ProviderCallFailedException>(() => Client(inner).GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        Assert.Contains("Primary DeepSeek", failure.Message, StringComparison.Ordinal);
        Assert.Contains("deepseek-reasoner", failure.Message, StringComparison.Ordinal);
        Assert.Contains("API key", failure.Message, StringComparison.Ordinal);
        Assert.Equal(AiProviderKind.OpenAiCompatible, failure.ProviderKind);
    }

    // A token that is already cancelled stops the call where it stands, so the provider is never asked. Counting
    // attempts cannot show anything about retrying here, which is why the pair below carries that question.
    [Fact]
    public async Task CancellationByTheCallerStopsBeforeTheProviderIsReached()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var inner = new ScriptedChatClient([new OperationCanceledException()]);

        await Assert.ThrowsAsync<OperationCanceledException>(() => Client(inner).GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")], cancellationToken: cts.Token));

        Assert.Equal(0, inner.Calls);
    }

    // The other half: a cancellation raised while the caller's token is healthy is still not ours to judge. It
    // reaches the caller as thrown, and the one attempt it cost is the only one made.
    [Fact]
    public async Task ACancellationRaisedWithAHealthyTokenIsNeitherRetriedNorRewritten()
    {
        var cancellation = new OperationCanceledException();
        var inner = new ScriptedChatClient([cancellation]);

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(() => Client(inner).GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        Assert.Same(cancellation, thrown);
        Assert.Equal(1, inner.Calls);
    }

    // A budget hard cap is thrown on the cancellation channel precisely so the orchestrator can recognise it by
    // type and publish partial findings. Wrapping it here would silently turn a governed stop into a crash.
    [Fact]
    public async Task ARefusalRidingTheCancellationChannelReachesItsHandlerUnchanged()
    {
        var inner = new ScriptedChatClient([new HardCapStub()]);

        await Assert.ThrowsAsync<HardCapStub>(() => Client(inner).GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        Assert.Equal(1, inner.Calls);
    }

    // The HTTP client's own timeout is the one cancellation that is a transport failure rather than an intent.
    [Fact]
    public async Task AnHttpClientTimeoutIsTreatedAsTransient()
    {
        var inner = new ScriptedChatClient([new TaskCanceledException("timed out", new TimeoutException()), null]);

        var response = await Client(inner).GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.Equal("ok", response.Text);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task TheBackoffDoublesAndStopsAtTheCeiling()
    {
        var clock = new RecordingTimeProvider();
        var policy = new ProviderRetryPolicy
        {
            MaxAttempts = 4,
            BaseDelay = TimeSpan.FromSeconds(2),
            MaxDelay = TimeSpan.FromSeconds(5),
            JitterFactor = 0,
        };
        var inner = new ScriptedChatClient([Transient(), Transient(), Transient(), null]);

        await Client(inner, policy, clock).GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.Equal(
            [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5)],
            clock.RequestedDelays);
    }

    // A provider is believed when it states its own backoff — but a Retry-After of half an hour must not park a
    // review job for half an hour, so the policy's ceiling still wins.
    [Fact]
    public async Task AProviderStatedDelayIsHonouredButStillCapped()
    {
        var clock = new RecordingTimeProvider();
        var inner = new ScriptedChatClient([Transient(), null]);
        var client = new ProviderRetryChatClient(
            inner,
            Immediate with { MaxAttempts = 2, MaxDelay = TimeSpan.FromSeconds(10) },
            _ => ProviderFailureVerdict.Transient("throttled", TimeSpan.FromMinutes(30), 429),
            Target,
            clock);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.Equal([TimeSpan.FromSeconds(10)], clock.RequestedDelays);
    }

    // A retried attempt has to send the same conversation. An enumerable evaluated a second time need not
    // produce the same messages, so it is materialised once before the first attempt.
    [Fact]
    public async Task ARetriedAttemptSendsTheSameConversation()
    {
        var sent = 0;
        var inner = new ScriptedChatClient([Transient(), null]);
        IEnumerable<ChatMessage> lazyMessages = Enumerable.Range(0, 1)
            .Select(_ => new ChatMessage(ChatRole.User, $"turn {++sent}"));

        await Client(inner).GetResponseAsync(lazyMessages);

        Assert.Equal(2, inner.Calls);
        Assert.All(inner.Conversations, conversation => Assert.Equal("turn 1", conversation[0].Text));
    }

    [Fact]
    public async Task StreamingRetriesWhileNothingHasBeenHandedOverYet()
    {
        var inner = new ScriptedChatClient([Transient(), null]);

        var updates = new List<string>();
        await foreach (var update in Client(inner).GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
        {
            updates.Add(update.Text);
        }

        Assert.Equal(["ok"], updates);
        Assert.Equal(2, inner.Calls);
    }

    // Once the caller has part of an answer, a second attempt would duplicate or contradict it. The failure is
    // reported instead — still as a provider failure with a cause, not as a raw SDK exception.
    [Fact]
    public async Task StreamingStopsRetryingOnceAnUpdateHasBeenDelivered()
    {
        var inner = new ScriptedChatClient([null], failAfterFirstUpdate: true);

        var updates = new List<string>();
        var failure = await Assert.ThrowsAsync<ProviderCallFailedException>(async () =>
        {
            await foreach (var update in Client(inner).GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
            {
                updates.Add(update.Text);
            }
        });

        Assert.Single(updates);
        Assert.Equal(1, inner.Calls);
        Assert.Equal(1, failure.Attempts);
    }

    // The classification is the driver's, not the retry wrapper's. A provider whose SDK reports throttling as its
    // own exception type is retried because its driver says so, with nothing here changed.
    [Fact]
    public async Task ADriverSpecificClassificationDecidesTheRetry()
    {
        var inner = new ScriptedChatClient([new InvalidOperationException("SDK throttle signal"), null]);
        var client = new ProviderRetryChatClient(
            inner,
            Immediate,
            _ => ProviderFailureVerdict.Transient("the driver knows this one is throttling"),
            Target);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.Equal("ok", response.Text);
        Assert.Equal(2, inner.Calls);
    }

    // A quota that stays exhausted for the whole attempt budget ends the call, and the reason written on the job
    // has to say it was a rate limit and what an operator can do about it.
    [Fact]
    public async Task AThrottleOnEveryAttemptEndsAsAFailureThatNamesTheRateLimit()
    {
        var inner = new ScriptedChatClient([Throttled(), Throttled(), Throttled()]);

        var failure = await Assert.ThrowsAsync<ProviderCallFailedException>(() => Client(inner).GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        Assert.Equal(3, inner.Calls);
        Assert.Equal(3, failure.Attempts);
        Assert.True(failure.Verdict.IsThrottled);
        Assert.Contains("throttled the request", failure.Message, StringComparison.Ordinal);
        Assert.Equal(DriverFailureMapper.ActionHintFor(429), failure.ActionHint);
        Assert.Contains(failure.ActionHint!, failure.Message, StringComparison.Ordinal);
    }

    private static ProviderRetryChatClient Client(
        IChatClient inner,
        ProviderRetryPolicy? policy = null,
        TimeProvider? timeProvider = null)
    {
        return new ProviderRetryChatClient(
            inner,
            policy ?? Immediate,
            DriverFailureMapper.ClassifyRuntimeFailure,
            Target,
            timeProvider);
    }

    private static Exception Transient()
    {
        return new HttpRequestException(HttpRequestError.ConnectionError, "connection reset");
    }

    private static Exception Permanent(int status = 400)
    {
        return new HttpRequestException("rejected", null, (System.Net.HttpStatusCode)status);
    }

    private static Exception Throttled()
    {
        return new HttpRequestException("rate limited", null, System.Net.HttpStatusCode.TooManyRequests);
    }

    /// <summary>Stands in for a budget hard cap: a product refusal that travels on the cancellation channel.</summary>
    private sealed class HardCapStub() : OperationCanceledException("the budget hard cap was reached");
}
