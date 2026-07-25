// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.Infrastructure.AI.Providers;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

public sealed class AiRuntimeFactoryTests
{
    private static readonly Guid ClientId = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000f0117");

    // Without a budget scope accessor, the raw driver client is returned unwrapped.
    [Fact]
    public void CreateChatRuntime_NoBudgetAccessor_ReturnsRawClient()
    {
        var (registry, driver, chatClient, connection, model, binding) = SetupChat();

        var factory = new AiRuntimeFactory(registry);
        var runtime = factory.CreateChatRuntime(connection, model, binding);

        Assert.Same(chatClient, runtime.ChatClient);
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

    // Without a budget scope accessor, the raw embedding generator is returned unwrapped.
    [Fact]
    public void CreateEmbeddingRuntime_NoBudgetAccessor_ReturnsRawGenerator()
    {
        var registry = Substitute.For<IAiProviderDriverRegistry>();
        var driver = Substitute.For<IAiProviderDriver>();
        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var model = AiConnectionTestFactory.CreateEmbeddingModel("embed-model");
        var binding = AiConnectionTestFactory.CreateBinding(AiPurpose.EmbeddingDefault, model, AiProtocolMode.Embeddings);
        var connection = AiConnectionTestFactory.CreateConnection(ClientId, [model], [binding]);
        registry.GetRequired(connection.ProviderKind).Returns(driver);
        driver.CreateEmbeddingGenerator(connection, model, binding, 1536).Returns(generator);

        var factory = new AiRuntimeFactory(registry);
        var runtime = factory.CreateEmbeddingRuntime(connection, model, binding, "cl100k_base", 1536);

        Assert.Same(generator, runtime.Generator);
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
        driver.CreateChatClient(connection, model, binding).Returns(chatClient);
        driver.GetChatRuntimeCapabilities(connection, model, binding)
            .Returns(new AgentReviewRuntimeCapabilities(true, true, true, true));
        return (registry, driver, chatClient, connection, model, binding);
    }
}
