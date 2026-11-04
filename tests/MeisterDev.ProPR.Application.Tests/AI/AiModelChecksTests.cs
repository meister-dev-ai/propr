// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Resilience;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using Microsoft.Extensions.AI;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Application.Tests.AI;

/// <summary>What a model check finds out about one configured model, and how little it asks of the provider.</summary>
public sealed class AiModelChecksTests
{
    private static readonly ProviderEndpoint Endpoint =
        new("meisterdev/openAiCompatible", "https://llm.example.test/v1", "apiKey", "key");

    private static readonly AiConfiguredModelDto Chat = new(
        Guid.NewGuid(), "gpt-6.1-terra", "Terra", [AiOperationKind.Chat], [ProviderDeclaredProtocolModes.Auto]);

    private static readonly AiConfiguredModelDto Embedding = new(
        Guid.NewGuid(), "bge-m3", "BGE", [AiOperationKind.Embedding],
        [ProviderDeclaredProtocolModes.Auto, ProviderDeclaredProtocolModes.Embeddings], "cl100k_base", 8191, 4);

    private readonly IAiProviderDriver _driver = Substitute.For<IAiProviderDriver>();
    private readonly IChatClient _chat = Substitute.For<IChatClient>();

    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddings =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

    public AiModelChecksTests()
    {
        this._driver.CreateChatClient(Endpoint, Arg.Any<ProviderModelDescriptor>(), Arg.Any<string>()).Returns(this._chat);
        this._driver.CreateEmbeddingGenerator(Endpoint, Arg.Any<ProviderModelDescriptor>(), Arg.Any<string>(), Arg.Any<int>())
            .Returns(this._embeddings);
        this._chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "OK")));
    }

    [Fact]
    public async Task AChatModelIsAskedForAFewTokensThroughAutomaticRouting()
    {
        var outcome = await AiModelChecks.CheckAsync(this._driver, Endpoint, Chat, AiOperationKind.Chat, CancellationToken.None);

        Assert.Equal(new AiModelCheckOutcomeDto(Chat.Id, "gpt-6.1-terra", AiOperationKind.Chat, true), outcome);
        this._driver.Received(1).CreateChatClient(
            Endpoint, Arg.Is<ProviderModelDescriptor>(model => model.RemoteModelId == "gpt-6.1-terra"),
            ProviderDeclaredProtocolModes.Auto);
        await this._chat.Received(1).GetResponseAsync(
            Arg.Is<IEnumerable<ChatMessage>>(messages => messages.Count() == 1),
            Arg.Is<ChatOptions?>(options => options!.MaxOutputTokens == AiModelChecks.MaximumOutputTokens),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnEmbeddingModelPassesWhenItsVectorHasTheConfiguredDimensions()
    {
        this.Embed(4);

        var outcome = await AiModelChecks.CheckAsync(this._driver, Endpoint, Embedding, AiOperationKind.Embedding, CancellationToken.None);

        Assert.True(outcome.Passed);
        Assert.Equal(4, outcome.ReturnedDimensions);
        this._driver.Received(1).CreateEmbeddingGenerator(Endpoint, Arg.Any<ProviderModelDescriptor>(), ProviderDeclaredProtocolModes.Embeddings, 4);
    }

    // The measured length is what an operator enters when the configured width is wrong, so it is reported.
    [Fact]
    public async Task AnEmbeddingModelWhoseVectorHasAnotherLengthFailsAndReportsTheLength()
    {
        this.Embed(1536);

        var outcome = await AiModelChecks.CheckAsync(this._driver, Endpoint, Embedding, AiOperationKind.Embedding, CancellationToken.None);

        Assert.Equal((false, AiModelCheckReasons.Dimensions, (int?)1536), (outcome.Passed, outcome.Reason, outcome.ReturnedDimensions));
        Assert.Contains("1536 dimensions", outcome.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEmbeddingCallThatReturnsNoVectorReportsNoLength()
    {
        this._embeddings.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>());

        var outcome = await AiModelChecks.CheckAsync(this._driver, Endpoint, Embedding, AiOperationKind.Embedding, CancellationToken.None);

        Assert.Equal((false, AiModelCheckReasons.Dimensions), (outcome.Passed, outcome.Reason));
        Assert.Null(outcome.ReturnedDimensions);
    }

    [Fact]
    public async Task AnEmbeddingModelWithoutDimensionsIsReportedWithoutCallingIt()
    {
        var outcome = await AiModelChecks.CheckAsync(
            this._driver, Endpoint, Embedding with { EmbeddingDimensions = null }, AiOperationKind.Embedding,
            CancellationToken.None);

        Assert.Equal((false, AiModelCheckReasons.Dimensions), (outcome.Passed, outcome.Reason));
        this._driver.DidNotReceiveWithAnyArgs().CreateEmbeddingGenerator(default!, default!, default!, default);
    }

    [Theory]
    [InlineData(401, AiModelCheckReasons.Credentials)]
    [InlineData(403, AiModelCheckReasons.ModelUnavailable)]
    [InlineData(404, AiModelCheckReasons.ModelUnavailable)]
    [InlineData(400, AiModelCheckReasons.Rejected)]
    [InlineData(429, AiModelCheckReasons.RateLimited)]
    [InlineData(503, AiModelCheckReasons.ProviderError)]
    public async Task TheStatusTheFamilyReadsOffItsClientDecidesTheReason(int status, string reason)
    {
        var refused = new InvalidOperationException($"HTTP {status} from the provider");
        this._chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(refused);
        this._driver.ClassifyRuntimeFailure(refused).Returns(ProviderFailureVerdict.Permanent("refused", status));

        var outcome = await AiModelChecks.CheckAsync(this._driver, Endpoint, Chat, AiOperationKind.Chat, CancellationToken.None);

        Assert.Equal((false, reason, (int?)status), (outcome.Passed, outcome.Reason, outcome.HttpStatus));
        Assert.Equal($"HTTP {status} from the provider", outcome.Summary);
        Assert.False(string.IsNullOrWhiteSpace(outcome.ActionHint));
    }

    [Fact]
    public async Task ACallThatGotNoAnswerIsUnreachable()
    {
        this._chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Name or service not known"));

        var outcome = await AiModelChecks.CheckAsync(this._driver, Endpoint, Chat, AiOperationKind.Chat, CancellationToken.None);

        Assert.Equal((false, AiModelCheckReasons.Unreachable), (outcome.Passed, outcome.Reason));
    }

    [Fact]
    public async Task AModelThatDoesNotAnswerInTimeIsUnreachable()
    {
        this._chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                return new ChatResponse();
            });

        var outcome = await AiModelChecks.CheckAsync(this._driver, Endpoint, Chat, AiOperationKind.Chat, CancellationToken.None, TimeSpan.FromMilliseconds(50));

        Assert.Equal((false, AiModelCheckReasons.Unreachable), (outcome.Passed, outcome.Reason));
        Assert.Contains("did not answer", outcome.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCancellationOfTheCallerPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        this._chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await cancellation.CancelAsync();
                await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                return new ChatResponse();
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AiModelChecks.CheckAsync(this._driver, Endpoint, Chat, AiOperationKind.Chat, cancellation.Token));
    }

    private void Embed(int dimensions)
    {
        this._embeddings.GenerateAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<EmbeddingGenerationOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new GeneratedEmbeddings<Embedding<float>>([new Embedding<float>(new float[dimensions])]));
    }
}
