// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Resilience;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.Ai.Providers.Drivers;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

public sealed class AiRuntimeFactoryTests
{
    private static readonly Guid ClientId = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000f0117");

    // Retry is not optional: a review that dies on one throttled call is not shippable, so the client is wrapped
    // even when nothing else contributes a stage. The resolved runtime still describes what it was built from.
    [Fact]
    public void CreateChatRuntime_NoBudgetAccessor_StillWrapsForRetry()
    {
        var (registry, _, chatClient, connection, model, binding) = SetupChat();

        var factory = new AiRuntimeFactory(registry);
        var runtime = factory.CreateChatRuntime(connection, model, binding);

        Assert.IsType<ProviderRetryChatClient>(runtime.ChatClient);
        Assert.NotSame(chatClient, runtime.ChatClient);
        Assert.Same(connection, runtime.Connection);
        Assert.Same(model, runtime.Model);
        Assert.Same(binding, runtime.Binding);
    }

    // With a budget scope accessor, the client is wrapped for metering (a different instance).
    [Fact]
    public void CreateChatRuntime_WithBudgetAccessor_WrapsClient()
    {
        var (registry, driver, chatClient, connection, model, binding) = SetupChat();
        var budgetAccessor = Substitute.For<IBudgetScopeAccessor>();

        var factory = new AiRuntimeFactory(registry, budgetAccessor);
        var runtime = factory.CreateChatRuntime(connection, model, binding);

        Assert.NotSame(chatClient, runtime.ChatClient);
    }

    // The retry budget comes from the review options rather than the library's default, so an operator raising
    // AI_MAX_RATE_LIMIT_RETRIES actually changes how many attempts a call gets.
    [Fact]
    public async Task CreateChatRuntime_RetriesUpToTheConfiguredAttemptCount()
    {
        var (registry, driver, _, connection, model, binding) = SetupChat();
        var throttled = new HttpRequestException(HttpRequestError.ConnectionError, "connection reset");
        driver.CreateChatClient(connection.ToProviderEndpoint(), model.ToProviderModel(), binding.ProtocolMode)
            .Returns(new AlwaysFailingChatClient(throttled));
        var options = Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxRateLimitRetries = 2, MaxBackoffSeconds = 5 });

        var factory = new AiRuntimeFactory(registry, aiOptions: options, timeProvider: new ImmediateTimeProvider());
        var runtime = factory.CreateChatRuntime(connection, model, binding);

        var failure =
            await Assert.ThrowsAsync<ProviderCallFailedException>(() => runtime.ChatClient.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));

        // Two retries on top of the first call, and the profile an operator would go and fix is named.
        Assert.Equal(3, failure.Attempts);
        Assert.Contains(connection.DisplayName, failure.Message, StringComparison.Ordinal);
    }

    // Embedding calls face the same provider quotas, so they are retried on the same policy.
    [Fact]
    public void CreateEmbeddingRuntime_NoBudgetAccessor_StillWrapsForRetry()
    {
        var registry = Substitute.For<IAiProviderDriverRegistry>();
        var driver = Substitute.For<IAiProviderDriver>();
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var model = AiConnectionTestFactory.CreateEmbeddingModel("embed-model");
        var binding = AiConnectionTestFactory.CreateBinding(AiPurpose.EmbeddingDefault, model, AiProtocolMode.Embeddings);
        var connection = AiConnectionTestFactory.CreateConnection(ClientId, [model], [binding]);
        registry.GetRequired(connection.ProviderKind).Returns(driver);
        driver.CreateEmbeddingGenerator(connection.ToProviderEndpoint(), model.ToProviderModel(), binding.ProtocolMode, 1536).Returns(generator);

        var factory = new AiRuntimeFactory(registry);
        var runtime = factory.CreateEmbeddingRuntime(connection, model, binding, "cl100k_base", 1536);

        Assert.IsType<ProviderRetryEmbeddingGenerator>(runtime.Generator);
        Assert.NotSame(generator, runtime.Generator);
        Assert.Equal("cl100k_base", runtime.TokenizerName);
        Assert.Equal(1536, runtime.Dimensions);
    }

    private static (IAiProviderDriverRegistry Registry, IAiProviderDriver Driver, IChatClient ChatClient,
        MeisterDev.ProPR.Application.DTOs.AiConnectionDto Connection, MeisterDev.ProPR.Application.DTOs.AiConfiguredModelDto Model,
        MeisterDev.ProPR.Application.DTOs.AiPurposeBindingDto Binding) SetupChat()
    {
        var registry = Substitute.For<IAiProviderDriverRegistry>();
        var driver = Substitute.For<IAiProviderDriver>();
        var chatClient = Substitute.For<IChatClient>();
        var model = AiConnectionTestFactory.CreateChatModel("gpt-x");
        var binding = AiConnectionTestFactory.CreateBinding(AiPurpose.ReviewDefault, model);
        var connection = AiConnectionTestFactory.CreateConnection(ClientId, [model], [binding]);
        registry.GetRequired(connection.ProviderKind).Returns(driver);
        driver.CreateChatClient(connection.ToProviderEndpoint(), model.ToProviderModel(), binding.ProtocolMode).Returns(chatClient);
        driver.GetChatRuntimeCapabilities(connection.ToProviderEndpoint(), model.ToProviderModel(), binding.ProtocolMode)
            .Returns(new ProviderRuntimeCapabilities(true, true, true, true));
        driver.ClassifyRuntimeFailure(Arg.Any<Exception>())
            .Returns(call => DriverFailureMapper.ClassifyRuntimeFailure(call.Arg<Exception>()));
        return (registry, driver, chatClient, connection, model, binding);
    }

    /// <summary>Collapses the backoff so a test about attempt counts does not spend the seconds it describes.</summary>
    private sealed class ImmediateTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            return new ImmediateTimer(callback, state, dueTime);
        }

        private sealed class ImmediateTimer : ITimer
        {
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private int _fired;

            public ImmediateTimer(TimerCallback callback, object? state, TimeSpan dueTime)
            {
                this._callback = callback;
                this._state = state;
                this.Change(dueTime, Timeout.InfiniteTimeSpan);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                // Queued rather than inline: the caller may still be wiring up what the callback completes.
                if (dueTime != Timeout.InfiniteTimeSpan && Interlocked.Exchange(ref this._fired, 1) == 0)
                {
                    ThreadPool.QueueUserWorkItem(_ => this._callback(this._state));
                }

                return true;
            }

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>Fails every call with the same exception, so attempt counting is observable.</summary>
    private sealed class AlwaysFailingChatClient(Exception failure) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<ChatResponse>(failure);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw failure;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public TService? GetService<TService>(object? key = null)
            where TService : class => null;

        public void Dispose()
        {
        }
    }
}
