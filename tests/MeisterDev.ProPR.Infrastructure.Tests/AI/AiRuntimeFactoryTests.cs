// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.Ai.Providers.Resilience;
using Microsoft.Extensions.AI;
using NSubstitute;
using MeisterDev.ProPR.TestSupport;

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

    // A gate is only worth having if the stage that uses it is actually in the composed chain: without it, a
    // fan-out learns about a throttle one refusal at a time, which is the behaviour the gate was added to end.
    [Fact]
    public void CreateChatRuntime_WithAThrottleGate_ComposesThePacingStage()
    {
        var (registry, _, _, connection, model, binding) = SetupChat();

        var factory = new AiRuntimeFactory(registry, throttleGate: new ProviderThrottleGate());
        var runtime = factory.CreateChatRuntime(connection, model, binding);

        Assert.IsType<ProviderRetryChatClient>(runtime.ChatClient);
        Assert.NotNull(runtime.ChatClient.GetService<ProviderPacingChatClient>());
    }

    // Nothing shared to pace against means no stage at all, rather than a gate built per scope that would only
    // ever tell a caller what it had already found out for itself.
    [Fact]
    public void CreateChatRuntime_WithNoThrottleGate_LeavesThePacingStageOut()
    {
        var (registry, _, _, connection, model, binding) = SetupChat();

        var factory = new AiRuntimeFactory(registry);
        var runtime = factory.CreateChatRuntime(connection, model, binding);

        Assert.Null(runtime.ChatClient.GetService<ProviderPacingChatClient>());
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
        driver.CreateChatClient(PointedAt(connection), model.ToProviderModel(), binding.ProtocolMode)
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
        var binding = AiConnectionTestFactory.CreateBinding(AiPurpose.EmbeddingDefault, model, ProviderDeclaredProtocolModes.Embeddings);
        var connection = AiConnectionTestFactory.CreateConnection(ClientId, [model], [binding]);
        registry.IsRegistered(connection.ProviderKind).Returns(true);
        registry.GetRequired(connection.ProviderKind).Returns(driver);
        driver.CreateEmbeddingGenerator(PointedAt(connection), model.ToProviderModel(), binding.ProtocolMode, 1536)
            .Returns(generator);

        var factory = new AiRuntimeFactory(registry);
        var runtime = factory.CreateEmbeddingRuntime(connection, model, binding, "cl100k_base", 1536);

        // Resolved through the composed generator rather than off its outermost type, because more than one
        // stage is contributed and only the retry stage is what this test is about.
        Assert.NotNull(runtime.Generator.GetService(typeof(ProviderRetryEmbeddingGenerator)));
        Assert.NotSame(generator, runtime.Generator);
        Assert.Equal("cl100k_base", runtime.TokenizerName);
        Assert.Equal(1536, runtime.Dimensions);
    }

    // The declared width is what the memory columns were provisioned for and what the resolver checks a purpose
    // against, and a deployment answers with its own width whatever the connection declares. Left unchecked the
    // difference reaches the operator as a rejected insert naming a column.
    [Fact]
    public async Task CreateEmbeddingRuntime_DeploymentAnswersAWidthTheConnectionDoesNotDeclare_RefusesNamingBoth()
    {
        var runtime = EmbeddingRuntimeOver(new FixedWidthEmbeddingGenerator(3072), declaredDimensions: 1536);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.Generator.GenerateAsync(["hello"]));

        Assert.Contains("3072", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("1536", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("embed-model", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateEmbeddingRuntime_DeploymentAnswersTheDeclaredWidth_PassesTheVectorThrough()
    {
        var runtime = EmbeddingRuntimeOver(new FixedWidthEmbeddingGenerator(1536), declaredDimensions: 1536);

        var generated = await runtime.Generator.GenerateAsync(["hello"]);

        Assert.Equal(1536, Assert.Single(generated).Vector.Length);
    }

    private static IResolvedAiEmbeddingRuntime EmbeddingRuntimeOver(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        int declaredDimensions)
    {
        var registry = Substitute.For<IAiProviderDriverRegistry>();
        var driver = Substitute.For<IAiProviderDriver>();
        var model = AiConnectionTestFactory.CreateEmbeddingModel("embed-model", declaredDimensions);
        var binding = AiConnectionTestFactory.CreateBinding(AiPurpose.EmbeddingDefault, model, ProviderDeclaredProtocolModes.Embeddings);
        var connection = AiConnectionTestFactory.CreateConnection(ClientId, [model], [binding]);
        registry.IsRegistered(connection.ProviderKind).Returns(true);
        registry.GetRequired(connection.ProviderKind).Returns(driver);
        driver.ClassifyRuntimeFailure(Arg.Any<Exception>())
            .Returns(call => DriverFailureMapper.ClassifyRuntimeFailure(call.Arg<Exception>()));
        driver
            .CreateEmbeddingGenerator(
                PointedAt(connection),
                model.ToProviderModel(),
                binding.ProtocolMode,
                declaredDimensions)
            .Returns(generator);

        return new AiRuntimeFactory(registry)
            .CreateEmbeddingRuntime(connection, model, binding, "cl100k_base", declaredDimensions);
    }

    /// <summary>A deployment that answers every input with a vector of one width, whatever was asked for.</summary>
    private sealed class FixedWidthEmbeddingGenerator(int width) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>([.. values.Select(_ => new Embedding<float>(new float[width]))]));
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
        }
    }

    // A profile stored against a family this build cannot name reports the enum's first member in ProviderKind,
    // so without this refusal the review would be routed to that member's driver on a credential configured for
    // something else. The stored identity is named because it is what has to be installed.
    [Fact]
    public void CreateChatRuntime_ConnectionWhoseFamilyIsNotInstalled_RefusesNamingTheConnectionAndTheIdentity()
    {
        var (registry, driver, _, connection, model, binding) = SetupChat();
        var unavailable = MarkUnavailable(connection, AiConnectionUnavailableReason.ProviderFamilyAbsent, "ContosoLlm");

        var factory = new AiRuntimeFactory(registry);
        var refusal = Assert.Throws<AiConnectionUnavailableException>(() => factory.CreateChatRuntime(unavailable, model, binding));

        Assert.Contains(unavailable.DisplayName, refusal.Message, StringComparison.Ordinal);
        Assert.Contains("ContosoLlm", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(unavailable.Id, refusal.ConnectionId);

        // Nothing was built, so the credential on the profile never reached a driver and there is no composed
        // pipeline for the retry or telemetry stages to act on.
        registry.DidNotReceiveWithAnyArgs().GetRequired(default);
        driver.DidNotReceiveWithAnyArgs().CreateChatClient(default!, default!, default!);
    }

    // The registry, not the enum, says which families this build can call, so a family it names and has no
    // driver for is refused the same way — and the refusal names the profile, which the registry's own lookup
    // failure cannot.
    [Fact]
    public void CreateChatRuntime_FamilyWithNoRegisteredDriver_RefusesNamingTheConnectionAndTheIdentity()
    {
        var (registry, driver, _, connection, model, binding) = SetupChat();
        registry.IsRegistered(connection.ProviderKind).Returns(false);

        var factory = new AiRuntimeFactory(registry);
        var refusal = Assert.Throws<AiConnectionUnavailableException>(() => factory.CreateChatRuntime(connection, model, binding));

        Assert.Contains(connection.DisplayName, refusal.Message, StringComparison.Ordinal);
        Assert.Contains(connection.ProviderKind, refusal.Message, StringComparison.Ordinal);
        registry.DidNotReceiveWithAnyArgs().GetRequired(default);
        driver.DidNotReceiveWithAnyArgs().CreateChatClient(default!, default!, default!);
    }

    // A stored vocabulary value this build cannot read leaves the profile describing something other than what
    // was configured, so it is refused too, and every unresolved value is named because each is its own fix.
    [Fact]
    public void CreateChatRuntime_ConnectionHoldingAnUnresolvableStoredValue_RefusesNamingTheValue()
    {
        var (registry, driver, _, connection, model, binding) = SetupChat();
        var unavailable = connection with
        {
            Availability = new AiConnectionAvailabilityDto(
                AiConnectionAvailabilityState.Unavailable,
                AiConnectionUnavailableReason.StoredValueUnresolved,
                null,
                [new AiUnresolvedValueDto(AiConnectionVocabularyField.AuthMode, "MutualTls")]),
        };

        var factory = new AiRuntimeFactory(registry);
        var refusal = Assert.Throws<AiConnectionUnavailableException>(() => factory.CreateChatRuntime(unavailable, model, binding));

        Assert.Contains("MutualTls", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(AiConnectionVocabularyField.AuthMode), refusal.Message, StringComparison.Ordinal);
        driver.DidNotReceiveWithAnyArgs().CreateChatClient(default!, default!, default!);
    }

    // The embedding path resolves the same profiles from the same store, so it refuses on the same terms.
    [Fact]
    public void CreateEmbeddingRuntime_ConnectionWhoseFamilyIsNotInstalled_Refuses()
    {
        var registry = Substitute.For<IAiProviderDriverRegistry>();
        var model = AiConnectionTestFactory.CreateEmbeddingModel("embed-model");
        var binding = AiConnectionTestFactory.CreateBinding(AiPurpose.EmbeddingDefault, model, ProviderDeclaredProtocolModes.Embeddings);
        var connection = MarkUnavailable(
            AiConnectionTestFactory.CreateConnection(ClientId, [model], [binding]),
            AiConnectionUnavailableReason.ProviderFamilyAbsent,
            "ContosoLlm");
        registry.IsRegistered(connection.ProviderKind).Returns(true);

        var factory = new AiRuntimeFactory(registry);
        var refusal = Assert.Throws<AiConnectionUnavailableException>(() => factory.CreateEmbeddingRuntime(connection, model, binding, "cl100k_base", 1536));

        Assert.Contains("ContosoLlm", refusal.Message, StringComparison.Ordinal);
        registry.DidNotReceiveWithAnyArgs().GetRequired(default);
    }

    // The refusal is a configuration problem, so it must not arrive as the failure type the retry stage raises
    // once a call has been repeated to exhaustion; a caller separating the two acts on the type.
    [Fact]
    public void CreateChatRuntime_RefusedConnection_DoesNotSurfaceAsAProviderCallFailure()
    {
        var (registry, _, _, connection, model, binding) = SetupChat();
        var unavailable = MarkUnavailable(connection, AiConnectionUnavailableReason.ProviderFamilyAbsent, "ContosoLlm");

        var factory = new AiRuntimeFactory(registry);
        var refusal = Record.Exception(() => factory.CreateChatRuntime(unavailable, model, binding));

        Assert.IsType<AiConnectionUnavailableException>(refusal);
        Assert.IsNotType<ProviderCallFailedException>(refusal);
    }

    // A profile whose stored values all resolve is built exactly as before the refusal existed.
    [Fact]
    public void CreateChatRuntime_AvailableConnection_BuildsTheRuntime()
    {
        var (registry, _, _, connection, model, binding) = SetupChat();

        var factory = new AiRuntimeFactory(registry);
        var runtime = factory.CreateChatRuntime(connection, model, binding);

        Assert.Same(connection, runtime.Connection);
        registry.Received(1).GetRequired(connection.ProviderKind);
    }

    private static MeisterDev.ProPR.Application.DTOs.AiConnectionDto MarkUnavailable(
        MeisterDev.ProPR.Application.DTOs.AiConnectionDto connection,
        AiConnectionUnavailableReason reason,
        string providerIdentity)
    {
        return connection with
        {
            Availability = new AiConnectionAvailabilityDto(
                AiConnectionAvailabilityState.Unavailable,
                reason,
                providerIdentity,
                []),
        };
    }

    /// <summary>The minimum a family states, and all the runtime stages read of one.</summary>
    private static ProviderDeclaration Declaration()
    {
        return new ProviderDeclaration
        {
            Key = "test/factory",
            Label = "Factory family",
            Version = "1.0",
            ContractVersion = ProviderContract.Version,
            AuthModes = [new ProviderDeclaredAuthMode("test/factory:ApiKey", [AiCredentialFieldSupport.ApiKey])],
            ProtocolModes = new ProviderDeclaredProtocolModes(["test/factory:ChatCompletions"]),
            ConformanceInputs = new ProviderConformanceInputs("test/factory:ApiKey"),
        };
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
        registry.IsRegistered(connection.ProviderKind).Returns(true);
        registry.GetRequired(connection.ProviderKind).Returns(driver);
        // Matched on where the endpoint points rather than by comparing two projections of the profile. An
        // endpoint carries the declared values as a dictionary, which compares by reference, so two projections
        // of one profile are equal only by accident.
        driver.CreateChatClient(PointedAt(connection), model.ToProviderModel(), binding.ProtocolMode).Returns(chatClient);
        driver.GetChatRuntimeCapabilities(PointedAt(connection), model.ToProviderModel(), binding.ProtocolMode)
            .Returns(new ProviderRuntimeCapabilities(true, true, true, true));

        // The stages read the family's declaration for the request shape it accepts and the usage mapping it
        // applies, so a driver with none of one is a driver the registry would not have handed over.
        driver.Declaration.Returns(Declaration());
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

    /// <summary>
    ///     Matches the endpoint a profile is projected onto by where it points.
    /// </summary>
    /// <remarks>
    ///     Compared on the family and the address rather than against a second projection of the same profile.
    ///     An endpoint carries its declared values as a dictionary, which compares by reference, so two
    ///     projections of one profile are equal only when they happen to share that instance.
    /// </remarks>
    /// <param name="connection">The profile the endpoint was projected from.</param>
    private static ProviderEndpoint PointedAt(AiConnectionDto connection)
    {
        return Arg.Is<ProviderEndpoint>(sent => sent.ProviderKind == connection.ProviderKind && sent.BaseUrl == connection.BaseUrl);
    }
}
