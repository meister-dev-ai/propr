// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.AI;
using Microsoft.Extensions.AI;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Unit tests for <see cref="ThreadMemoryEmbedder" /> (T017 / T040).
/// </summary>
public sealed class ThreadMemoryEmbedderTests
{
    private static readonly Guid ClientId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    private static AiConnectionDto ValidEmbeddingConnection(bool includeBinding = true)
    {
        return AiConnectionTestFactory.CreateEmbeddingConnection(ClientId, includeBinding: includeBinding);
    }

    private static ThreadMemoryEmbedder BuildEmbedder(
        IAiConnectionRepository? aiConnRepo = null,
        IAiEmbeddingGeneratorFactory? factory = null,
        IChatClient? chatClient = null)
    {
        var repository = aiConnRepo ?? Substitute.For<IAiConnectionRepository>();
        var opts = Microsoft.Extensions.Options.Options.Create(new AiReviewOptions());
        return new ThreadMemoryEmbedder(
            repository,
            factory ?? Substitute.For<IAiEmbeddingGeneratorFactory>(),
            new EmbeddingDeploymentResolver(repository),
            opts,
            chatClient ?? Substitute.For<IChatClient>());
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_NormalCase_ReturnsFloatArrayFromGenerator()
    {
        var expectedFloats = new[] { 0.1f, 0.2f, 0.3f };

        var aiConnRepo = Substitute.For<IAiConnectionRepository>();
        aiConnRepo.GetForTierAsync(ClientId, AiConnectionModelCategory.Embedding, Arg.Any<CancellationToken>())
            .Returns(ValidEmbeddingConnection());

        var generator = Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();
        var embedding = new Embedding<float>(expectedFloats);
        generator.GenerateAsync(
                Arg.Any<IEnumerable<string>>(),
                Arg.Any<EmbeddingGenerationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([embedding]));

        var factory = Substitute.For<IAiEmbeddingGeneratorFactory>();
        factory.CreateGenerator(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>())
            .Returns(generator);

        var embedder = BuildEmbedder(aiConnRepo, factory);

        var result = await embedder.GenerateEmbeddingAsync("some text", ClientId);

        Assert.Equal(expectedFloats, result);
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_NoEmbeddingConnection_ThrowsInvalidOperation()
    {
        var aiConnRepo = Substitute.For<IAiConnectionRepository>();
        aiConnRepo.GetForTierAsync(ClientId, AiConnectionModelCategory.Embedding, Arg.Any<CancellationToken>())
            .Returns((AiConnectionDto?)null);

        var embedder = BuildEmbedder(aiConnRepo);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            embedder.GenerateEmbeddingAsync("some text", ClientId));
    }

    [Fact]
    public async Task GenerateEmbeddingAsync_MissingEmbeddingBinding_ThrowsWithoutConfiguredModelFallback()
    {
        var aiConnRepo = Substitute.For<IAiConnectionRepository>();
        aiConnRepo.GetForTierAsync(ClientId, AiConnectionModelCategory.Embedding, Arg.Any<CancellationToken>())
            .Returns(ValidEmbeddingConnection(includeBinding: false));

        var factory = Substitute.For<IAiEmbeddingGeneratorFactory>();

        var embedder = BuildEmbedder(aiConnRepo, factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            embedder.GenerateEmbeddingAsync("some text", ClientId));

        factory.DidNotReceiveWithAnyArgs()
            .CreateGenerator(default!, default!, default, default);
    }

    [Fact]
    public async Task GenerateResolutionSummaryAsync_WhenChatClientThrows_ReturnsFallbackSummaryWithoutThrowing()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("AI endpoint unreachable"));

        var embedder = BuildEmbedder(chatClient: chatClient);

        var ex = await Record.ExceptionAsync(() =>
            embedder.GenerateResolutionSummaryAsync("src/Foo.cs", "diff content", "comment history", ClientId));

        Assert.Null(ex);
    }

    [Fact]
    public async Task GenerateResolutionSummaryAsync_WhenChatClientThrows_ReturnsFallbackText()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("AI endpoint unreachable"));

        var embedder = BuildEmbedder(chatClient: chatClient);
        var result = await embedder.GenerateResolutionSummaryAsync("src/Foo.cs", null, "comment history", ClientId);

        Assert.False(string.IsNullOrEmpty(result));
    }

    [Fact]
    public async Task GenerateResolutionSummaryAsync_NormalCase_ReturnsChatClientResponse()
    {
        IEnumerable<ChatMessage>? capturedMessages = null;
        var chatClient = Substitute.For<IChatClient>();
        var message = new ChatMessage(ChatRole.Assistant, "This was resolved by adding nil check.");
        chatClient.GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(m => capturedMessages = m),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([message]));

        var embedder = BuildEmbedder(chatClient: chatClient);
        var result = await embedder.GenerateResolutionSummaryAsync("src/Foo.cs", "diff", "comment history", ClientId);

        Assert.Equal("This was resolved by adding nil check.", result);

        // Assert the system prompt instructs the model to address mechanism, specific change, and rationale
        Assert.NotNull(capturedMessages);
        var systemContent = capturedMessages!.First(m => m.Role == ChatRole.System).Text;
        Assert.Contains("code change", systemContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("specifically", systemContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("accepted", systemContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerateResolutionSummaryAsync_WhenNoChangeExcerpt_UserPromptIncludesNoExcerptHint()
    {
        IEnumerable<ChatMessage>? capturedMessages = null;
        var chatClient = Substitute.For<IChatClient>();
        var message = new ChatMessage(ChatRole.Assistant, "Resolved without code change.");
        chatClient.GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(m => capturedMessages = m),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse([message]));

        var embedder = BuildEmbedder(chatClient: chatClient);
        await embedder.GenerateResolutionSummaryAsync("src/Foo.cs", null, "Alice: looks fine", ClientId);

        Assert.NotNull(capturedMessages);
        var userContent = capturedMessages!.First(m => m.Role == ChatRole.User).Text;
        Assert.Contains("No diff excerpt", userContent, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Change excerpt:", userContent, StringComparison.OrdinalIgnoreCase);
    }
}
