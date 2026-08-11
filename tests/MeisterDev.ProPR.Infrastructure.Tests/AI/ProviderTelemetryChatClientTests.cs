// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Resilience;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

/// <summary>
///     Covers what a model call leaves behind. Spend and failure have to be attributable to the provider that
///     produced them; a span that names only the model cannot distinguish the same model reached two ways.
/// </summary>
public sealed class ProviderTelemetryChatClientTests : IDisposable
{
    // An ActivityListener is process-wide and the infrastructure source is shared, so anything else emitting on
    // it while this class runs would land in the same list. Each instance therefore tags its calls with a model
    // id nobody else uses and keeps only those — without it, asserting on a single activity is a race that shows
    // up as an occasional failure in the full suite and never when the class is run alone.
    private readonly string _modelId = $"deepseek-reasoner-{Guid.NewGuid():N}";
    private readonly ProviderCallTarget _target;

    private readonly List<Activity> _captured = [];
    private readonly List<string> _recordedOutcomes = [];
    private readonly ActivityListener _listener;
    private readonly MeterListener _meterListener;
    private readonly AiProviderMetrics _metrics = new();

    public ProviderTelemetryChatClientTests()
    {
        this._target = new ProviderCallTarget(AiProviderKind.OpenAiCompatible, this._modelId, "Primary DeepSeek");
        this._listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "MeisterProPR.Infrastructure",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (this._captured)
                {
                    this._captured.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(this._listener);

        // The meter is process-wide for the same reason the activity source is, so the call counter is read back
        // through the same filter: only measurements tagged with this instance's model id count as its own.
        this._meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name == "meisterpropr_ai_provider_calls_total")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        this._meterListener.SetMeasurementEventCallback<long>((_, _, tags, _) => this.RecordOutcome(tags));
        this._meterListener.Start();
    }

    /// <summary>Only this instance's own calls, so a concurrent test on the shared source cannot be mistaken for one.</summary>
    private List<Activity> Activities
    {
        get
        {
            lock (this._captured)
            {
                return [.. this._captured.Where(activity => (activity.GetTagItem("ai_model") as string) == this._modelId)];
            }
        }
    }

    /// <summary>The outcome tag of every call this instance recorded, in order.</summary>
    private List<string> Outcomes
    {
        get
        {
            lock (this._recordedOutcomes)
            {
                return [.. this._recordedOutcomes];
            }
        }
    }

    public void Dispose()
    {
        this._listener.Dispose();
        this._meterListener.Dispose();
        this._metrics.Dispose();
    }

    [Fact]
    public async Task ACallIsRecordedWithTheProviderTheModelAndTheProfile()
    {
        var client = this.Client(new StubChatClient(Usage(input: 120, output: 30)));

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        var activity = Assert.Single(this.Activities);
        Assert.Equal("ai.provider.chat", activity.OperationName);
        Assert.Equal("OpenAiCompatible", Tag(activity, "ai_provider"));
        Assert.Equal(this._modelId, Tag(activity, "ai_model"));
        Assert.Equal("Primary DeepSeek", Tag(activity, "ai_profile"));
        Assert.Equal(ActivityStatusCode.Ok, activity.Status);
    }

    // Cache and reasoning counters are on the span, not only in the daily aggregate, so a single expensive call
    // can be explained rather than only noticed at the end of the day.
    [Fact]
    public async Task TheFullTokenBreakdownAndTheCostLandOnTheSpan()
    {
        var usage = Usage(input: 1_000, output: 200);
        usage.CachedInputTokenCount = 400;
        usage.ReasoningTokenCount = 150;
        usage.AdditionalCounts = new AdditionalPropertiesDictionary<long> { ["cache_creation_input_tokens"] = 100 };
        var client = this.Client(new StubChatClient(usage), new ModelPricing(3m, 15m, 0.75m, 3.75m));

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        var activity = Assert.Single(this.Activities);
        Assert.Equal(1_000L, activity.GetTagItem("ai_input_tokens"));
        Assert.Equal(200L, activity.GetTagItem("ai_output_tokens"));
        Assert.Equal(400L, activity.GetTagItem("ai_cached_input_tokens"));
        Assert.Equal(100L, activity.GetTagItem("ai_cache_write_tokens"));
        Assert.Equal(150L, activity.GetTagItem("ai_reasoning_tokens"));
        Assert.True((bool?)activity.GetTagItem("ai_usage_measured") ?? false);
        Assert.NotNull(activity.GetTagItem("ai_cost_usd"));
    }

    // A response with no usage payload is flagged rather than reported as a call that cost nothing.
    [Fact]
    public async Task AResponseWithNoUsagePayloadIsMarkedUnmeasured()
    {
        var client = this.Client(new StubChatClient(usage: null));

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        var activity = Assert.Single(this.Activities);
        Assert.False((bool?)activity.GetTagItem("ai_usage_measured") ?? true);
    }

    [Fact]
    public async Task AFailedCallLeavesAnErrorSpanNamingTheExceptionAndStillThrows()
    {
        var client = this.Client(new StubChatClient(new InvalidOperationException("provider said no")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        var activity = Assert.Single(this.Activities);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("System.InvalidOperationException", Tag(activity, "error.type"));
    }

    // A stopped job and a budget refusal both arrive as cancellation. Marking those as provider errors would make
    // a governed stop look like an outage in whatever dashboard reads the span status.
    [Fact]
    public async Task ACancelledCallIsNotMarkedAsAProviderError()
    {
        var client = this.Client(new StubChatClient(new OperationCanceledException()));

        await Assert.ThrowsAsync<OperationCanceledException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        var activity = Assert.Single(this.Activities);
        Assert.NotEqual(ActivityStatusCode.Error, activity.Status);
    }

    // A throttle is the provider working exactly as configured and saying "not right now". The retry stage waits
    // and asks again, so recording it as a provider error would show a recovered review as an outage.
    [Fact]
    public async Task AThrottleIsRecordedAsThrottlingRatherThanAsAProviderError()
    {
        var client = this.Client(
            new StubChatClient(new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests)),
            classifyFailure: _ => ProviderFailureVerdict.Throttled("the provider throttled the request", TimeSpan.FromSeconds(4), 429));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        var activity = Assert.Single(this.Activities);
        Assert.NotEqual(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(["throttled"], this.Outcomes);

        // Backends derive an error rate from this tag rather than from the span status, so leaving it on a
        // throttle would keep counting the rate limit as an outage whatever the status says.
        Assert.Null(activity.GetTagItem("error.type"));
    }

    [Fact]
    public async Task AFailureTheDriverDoesNotCallAThrottleIsStillAProviderError()
    {
        var client = this.Client(
            new StubChatClient(new InvalidOperationException("provider said no")),
            classifyFailure: _ => ProviderFailureVerdict.Permanent("the provider rejected the request", 400));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        var activity = Assert.Single(this.Activities);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(["error"], this.Outcomes);
    }

    // Without a classification there is nothing to distinguish a throttle by, and guessing would be worse than
    // the honest answer that the call failed.
    [Fact]
    public async Task WithNoClassificationEveryFailureIsStillAnError()
    {
        var client = this.Client(new StubChatClient(new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests)));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        Assert.Equal(["error"], this.Outcomes);
    }

    // This stage sees one attempt, never the call as a whole, so what is pinned here is the attempt: its throttle
    // line is written without an exception argument, which is what keeps a stack trace out of the log. That the
    // call then recovers is a question for the two stages together, covered further down.
    [Fact]
    public async Task AThrottledAttemptIsLoggedWithNoStackTraceBehindIt()
    {
        var logger = new CapturingLogger();
        var client = this.Client(
            new StubChatClient(new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests)),
            logger: logger,
            classifyFailure: _ => ProviderFailureVerdict.Throttled("the provider throttled the request", TimeSpan.FromSeconds(4), 429));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        var entry = Assert.Single(logger.Entries);
        Assert.Null(entry.Exception);
        Assert.Contains("throttled", entry.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AGenuineFaultIsStillLoggedWithTheExceptionThatCausedIt()
    {
        var logger = new CapturingLogger();
        var failure = new InvalidOperationException("provider said no");
        var client = this.Client(new StubChatClient(failure), logger: logger);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        var entry = Assert.Single(logger.Entries);
        Assert.Same(failure, entry.Exception);
    }

    // A classifier is the driver's own code. Recording a call is a side errand, so one that throws must not take
    // the provider failure with it before the retry stage has had a look.
    [Fact]
    public async Task AClassifierThatThrowsLeavesTheProviderFailureOnItsWayUp()
    {
        var failure = new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests);
        var client = this.Client(
            new StubChatClient(failure),
            classifyFailure: _ => throw new InvalidOperationException("the classifier is broken"));

        var thrown = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        Assert.Same(failure, thrown);
        Assert.Equal(["error"], this.Outcomes);
    }

    private static UsageDetails Usage(long input, long output)
    {
        return new UsageDetails { InputTokenCount = input, OutputTokenCount = output };
    }

    private static string? Tag(Activity activity, string name)
    {
        return activity.GetTagItem(name) as string;
    }

    // Absorption is a property of the pair, not of this stage: the throttled attempt is measured and logged as a
    // throttle, and the caller still gets an answer, with nothing in the log carrying a trace for either attempt.
    [Fact]
    public async Task AThrottleTheRetryStageAbsorbsStillLeavesAnAnswerAndNoLoggedFault()
    {
        var logger = new CapturingLogger();
        var inner = new ThrottleThenAnswerChatClient(new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests));
        var measured = this.Client(inner, classifyFailure: ThrottledWithoutStatedWait, logger: logger);

        // No stated wait and no base backoff, so the retry lands at once and the test does not sit on a clock.
        var policy = new ProviderRetryPolicy
        {
            MaxAttempts = 2,
            BaseDelay = TimeSpan.Zero,
            MaxDelay = TimeSpan.FromSeconds(30),
            JitterFactor = 0,
        };
        var client = new ProviderRetryChatClient(measured, policy, ThrottledWithoutStatedWait, this._target);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.Equal("ok", response.Text);
        Assert.Equal(2, inner.Calls);
        Assert.Equal(["throttled", "ok"], this.Outcomes);
        Assert.All(logger.Entries, entry => Assert.Null(entry.Exception));
    }

    private static ProviderFailureVerdict ThrottledWithoutStatedWait(Exception exception)
    {
        return ProviderFailureVerdict.Throttled("the provider throttled the request", null, 429);
    }

    private ProviderTelemetryChatClient Client(
        IChatClient inner,
        ModelPricing? pricing = null,
        Func<Exception, ProviderFailureVerdict>? classifyFailure = null,
        ILogger? logger = null)
    {
        return new ProviderTelemetryChatClient(
            inner,
            this._target,
            pricing ?? new ModelPricing(3m, 15m),
            this._metrics,
            logger,
            clientId: Guid.NewGuid(),
            classifyFailure: classifyFailure);
    }

    private void RecordOutcome(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        string? model = null;
        string? outcome = null;
        foreach (var tag in tags)
        {
            if (tag.Key == "ai_model")
            {
                model = tag.Value as string;
            }
            else if (tag.Key == "outcome")
            {
                outcome = tag.Value as string;
            }
        }

        if (model != this._modelId || outcome is null)
        {
            return;
        }

        lock (this._recordedOutcomes)
        {
            this._recordedOutcomes.Add(outcome);
        }
    }

    /// <summary>
    ///     Keeps each line and whatever exception was handed to it, which is what tells an absorbed throttle from
    ///     a fault the operator is meant to see a trace for.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<(string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);

            this.Entries.Add((formatter(state, exception), exception));
        }
    }

    /// <summary>Refuses the first call and answers the second, which is the shape of a throttle worth absorbing.</summary>
    /// <param name="failure">What the first call throws.</param>
    private sealed class ThrottleThenAnswerChatClient(Exception failure) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            this.Calls++;
            return this.Calls == 1
                ? Task.FromException<ChatResponse>(failure)
                : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("This client serves the response path only.");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public TService? GetService<TService>(object? key = null)
            where TService : class => null;

        public void Dispose()
        {
        }
    }

    private sealed class StubChatClient : IChatClient
    {
        private readonly UsageDetails? _usage;
        private readonly Exception? _failure;

        public StubChatClient(UsageDetails? usage)
        {
            this._usage = usage;
        }

        public StubChatClient(Exception failure)
        {
            this._failure = failure;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (this._failure is not null)
            {
                return Task.FromException<ChatResponse>(this._failure);
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")) { Usage = this._usage });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public TService? GetService<TService>(object? key = null)
            where TService : class => null;

        public void Dispose()
        {
        }
    }
}
