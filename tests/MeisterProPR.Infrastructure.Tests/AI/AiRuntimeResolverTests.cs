// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.AI;
using MeisterProPR.Infrastructure.AI.Providers;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class AiRuntimeResolverTests
{
    private static readonly Guid ClientId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000042");

    [Fact]
    public async Task ResolveChatRuntimeAsync_UsesPurposeBindingWithoutReadingGenericActiveProfile()
    {
        var repository = Substitute.For<IAiConnectionRepository>();
        var providerRegistry = Substitute.For<IAiProviderDriverRegistry>();
        var driver = Substitute.For<IAiProviderDriver>();
        var chatClient = Substitute.For<IChatClient>();
        var model = AiConnectionTestFactory.CreateChatModel("gpt-4.1");
        var binding = AiConnectionTestFactory.CreateBinding(AiPurpose.ReviewDefault, model);
        var connection = AiConnectionTestFactory.CreateConnection(ClientId, [model], [binding]);

        repository.GetActiveBindingForPurposeAsync(ClientId, AiPurpose.ReviewDefault, Arg.Any<CancellationToken>())
            .Returns(new AiResolvedPurposeBindingDto(connection, model, binding));
        providerRegistry.GetRequired(connection.ProviderKind).Returns(driver);
        driver.CreateChatClient(connection, model, binding).Returns(chatClient);
        driver.GetChatRuntimeCapabilities(connection, model, binding)
            .Returns(new AgentReviewRuntimeCapabilities(true, true, true, true));

        var resolver = new AiRuntimeResolver(repository, providerRegistry);

        var runtime = await resolver.ResolveChatRuntimeAsync(ClientId, AiPurpose.ReviewDefault, CancellationToken.None);

        Assert.Same(connection, runtime.Connection);
        Assert.Same(model, runtime.Model);
        Assert.Same(binding, runtime.Binding);
        Assert.Same(chatClient, runtime.ChatClient);
        Assert.Equal(new AgentReviewRuntimeCapabilities(true, true, true, true), runtime.Capabilities);
        await repository.DidNotReceive().GetActiveForClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveChatRuntimeForModelAsync_BuildsRuntimeFromModelBinding()
    {
        var repository = Substitute.For<IAiConnectionRepository>();
        var providerRegistry = Substitute.For<IAiProviderDriverRegistry>();
        var driver = Substitute.For<IAiProviderDriver>();
        var chatClient = Substitute.For<IChatClient>();
        var model = AiConnectionTestFactory.CreateChatModel("gpt-5.3-codex");
        var binding = AiConnectionTestFactory.CreateBinding(AiPurpose.ReviewDefault, model);
        var connection = AiConnectionTestFactory.CreateConnection(ClientId, [model], [binding]);

        repository.GetModelBindingAsync(ClientId, model.Id, Arg.Any<CancellationToken>())
            .Returns(new AiResolvedPurposeBindingDto(connection, model, binding));
        providerRegistry.GetRequired(connection.ProviderKind).Returns(driver);
        driver.CreateChatClient(connection, model, binding).Returns(chatClient);
        driver.GetChatRuntimeCapabilities(connection, model, binding)
            .Returns(new AgentReviewRuntimeCapabilities(true, true, true, true));

        var resolver = new AiRuntimeResolver(repository, providerRegistry);

        var runtime = await resolver.ResolveChatRuntimeForModelAsync(ClientId, model.Id, CancellationToken.None);

        Assert.Same(connection, runtime.Connection);
        Assert.Same(model, runtime.Model);
        Assert.Same(chatClient, runtime.ChatClient);
        Assert.Equal(new AgentReviewRuntimeCapabilities(true, true, true, true), runtime.Capabilities);
        await repository.DidNotReceive().GetActiveBindingForPurposeAsync(Arg.Any<Guid>(), Arg.Any<AiPurpose>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveChatRuntimeForModelAsync_UnknownModel_Throws()
    {
        var repository = Substitute.For<IAiConnectionRepository>();
        var providerRegistry = Substitute.For<IAiProviderDriverRegistry>();
        var missingModelId = Guid.NewGuid();

        repository.GetModelBindingAsync(ClientId, missingModelId, Arg.Any<CancellationToken>())
            .Returns((AiResolvedPurposeBindingDto?)null);

        var resolver = new AiRuntimeResolver(repository, providerRegistry);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveChatRuntimeForModelAsync(ClientId, missingModelId, CancellationToken.None));

        Assert.Contains(missingModelId.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveEmbeddingRuntimeAsync_MissingPurposeBinding_ThrowsWithoutGenericFallback()
    {
        var repository = Substitute.For<IAiConnectionRepository>();
        var providerRegistry = Substitute.For<IAiProviderDriverRegistry>();

        repository.GetActiveBindingForPurposeAsync(ClientId, AiPurpose.EmbeddingDefault, Arg.Any<CancellationToken>())
            .Returns((AiResolvedPurposeBindingDto?)null);
        repository.GetActiveForClientAsync(ClientId, Arg.Any<CancellationToken>())
            .Returns(AiConnectionTestFactory.CreateEmbeddingConnection(ClientId));

        var resolver = new AiRuntimeResolver(repository, providerRegistry);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveEmbeddingRuntimeAsync(ClientId, AiPurpose.EmbeddingDefault, 1536, CancellationToken.None));

        Assert.Contains("No active AI binding is configured", exception.Message, StringComparison.Ordinal);
        await repository.DidNotReceive().GetActiveForClientAsync(ClientId, Arg.Any<CancellationToken>());
    }
}
