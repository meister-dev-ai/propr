// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.AI;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

/// <summary>
///     Covers what a model call leaves behind. Spend and failure have to be attributable to the provider that
///     produced them; a span that names only the model cannot distinguish the same model reached two ways.
/// </summary>
public sealed class ProviderTelemetryChatClientTests : IDisposable
{
    private static readonly ProviderCallTarget Target =
        new(AiProviderKind.OpenAiCompatible, "deepseek-reasoner", "Primary DeepSeek");

    private readonly List<Activity> _activities = [];
    private readonly ActivityListener _listener;
    private readonly AiProviderMetrics _metrics = new();

    public ProviderTelemetryChatClientTests()
    {
        this._listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "MeisterProPR.Infrastructure",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => this._activities.Add(activity),
        };
        ActivitySource.AddActivityListener(this._listener);
    }

    public void Dispose()
    {
        this._listener.Dispose();
        this._metrics.Dispose();
    }

    [Fact]
    public async Task ACallIsRecordedWithTheProviderTheModelAndTheProfile()
    {
        var client = this.Client(new StubChatClient(Usage(input: 120, output: 30)));

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        var activity = Assert.Single(this._activities);
        Assert.Equal("ai.provider.chat", activity.OperationName);
        Assert.Equal("OpenAiCompatible", Tag(activity, "ai_provider"));
        Assert.Equal("deepseek-reasoner", Tag(activity, "ai_model"));
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

        var activity = Assert.Single(this._activities);
        Assert.Equal(1_000L, activity.GetTagItem("ai_input_tokens"));
        Assert.Equal(200L, activity.GetTagItem("ai_output_tokens"));
        Assert.Equal(400L, activity.GetTagItem("ai_cached_input_tokens"));
        Assert.Equal(100L, activity.GetTagItem("ai_cache_write_tokens"));
        Assert.Equal(150L, activity.GetTagItem("ai_reasoning_tokens"));
        Assert.Equal(true, activity.GetTagItem("ai_usage_measured"));
        Assert.NotNull(activity.GetTagItem("ai_cost_usd"));
    }

    // A response with no usage payload is flagged rather than reported as a call that cost nothing.
    [Fact]
    public async Task AResponseWithNoUsagePayloadIsMarkedUnmeasured()
    {
        var client = this.Client(new StubChatClient(usage: null));

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        var activity = Assert.Single(this._activities);
        Assert.Equal(false, activity.GetTagItem("ai_usage_measured"));
    }

    [Fact]
    public async Task AFailedCallLeavesAnErrorSpanNamingTheExceptionAndStillThrows()
    {
        var client = this.Client(new StubChatClient(new InvalidOperationException("provider said no")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        var activity = Assert.Single(this._activities);
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

        var activity = Assert.Single(this._activities);
        Assert.NotEqual(ActivityStatusCode.Error, activity.Status);
    }

    private static UsageDetails Usage(long input, long output)
    {
        return new UsageDetails { InputTokenCount = input, OutputTokenCount = output };
    }

    private static string? Tag(Activity activity, string name)
    {
        return activity.GetTagItem(name) as string;
    }

    private ProviderTelemetryChatClient Client(IChatClient inner, ModelPricing? pricing = null)
    {
        return new ProviderTelemetryChatClient(
            inner,
            Target,
            pricing ?? new ModelPricing(3m, 15m),
            this._metrics,
            clientId: Guid.NewGuid());
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
