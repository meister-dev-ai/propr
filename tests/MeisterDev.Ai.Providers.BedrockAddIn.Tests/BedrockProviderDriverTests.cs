// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using Amazon.Bedrock;
using Amazon.Bedrock.Model;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterDev.Ai.Providers.BedrockAddIn.Tests;

/// <summary>
///     Covers the Bedrock driver against substituted AWS clients: where it will and will not send traffic, what
///     it makes of the account's model list, and how it classifies the failures AWS reports its own way.
/// </summary>
public sealed class BedrockProviderDriverTests
{
    [Fact]
    public async Task TheAccountsModelsAreListedAndSortedIntoWhatEachCanDo()
    {
        var control = Substitute.For<IAmazonBedrock>();
        control.ListFoundationModelsAsync(Arg.Any<ListFoundationModelsRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new ListFoundationModelsResponse
                {
                    ModelSummaries =
                    [
                        Summary("anthropic.claude-opus-4-5", "Claude Opus 4.5", "Anthropic", "TEXT"),
                        Summary("amazon.titan-embed-text-v2:0", "Titan Text Embeddings V2", "Amazon", "EMBEDDING"),
                        Summary("stability.sd3-5-large-v1:0", "Stable Diffusion 3.5", "Stability AI", "IMAGE"),
                    ],
                });

        var result = await Driver(control: control).DiscoverModelsAsync(Endpoint());

        Assert.Equal("succeeded", result.DiscoveryStatus);
        var chat = result.Models.Single(model => model.RemoteModelId == "anthropic.claude-opus-4-5");
        Assert.Contains(AiOperationKind.Chat, chat.OperationKinds);
        Assert.Contains(BedrockProviderDriver.ConverseProtocol, chat.SupportedProtocolModes);

        var embedding = result.Models.Single(model => model.RemoteModelId == "amazon.titan-embed-text-v2:0");
        Assert.Contains(AiOperationKind.Embedding, embedding.OperationKinds);
        Assert.Contains(ProviderDeclaredProtocolModes.Embeddings, embedding.SupportedProtocolModes);

        // An image model has no place in a review, and offering one would only produce a call that cannot work.
        Assert.DoesNotContain(result.Models, model => model.RemoteModelId.StartsWith("stability.", StringComparison.Ordinal));
    }

    // Many Bedrock models are only callable through an inference profile, and the model list does not say which.
    // An operator who hits that gets a rejection naming nothing; being told up front is the cheaper lesson.
    [Fact]
    public async Task DiscoveryWarnsThatSomeModelsAnswerOnlyThroughAnInferenceProfile()
    {
        var control = Substitute.For<IAmazonBedrock>();
        control.ListFoundationModelsAsync(Arg.Any<ListFoundationModelsRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new ListFoundationModelsResponse
                {
                    ModelSummaries = [Summary("anthropic.claude-opus-4-5", "Claude Opus 4.5", "Anthropic", "TEXT")],
                });

        var result = await Driver(control: control).DiscoverModelsAsync(Endpoint());

        Assert.Contains(result.Warnings, warning => warning.Contains("inference profile", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AVpcEndpointDiscoversNothingAndSaysWhyRatherThanFailing()
    {
        var factory = Substitute.For<IBedrockClientFactory>();
        factory.CreateControlPlaneClient(Arg.Any<ProviderEndpoint>()).Returns((IAmazonBedrock?)null);

        var result = await new BedrockProviderDriver(factory).DiscoverModelsAsync(Endpoint("https://vpce-0abc.bedrock-runtime.eu-central-1.vpce.example.com"));

        Assert.Equal("succeeded", result.DiscoveryStatus);
        Assert.Empty(result.Models);
        Assert.Contains(result.Warnings, warning => warning.Contains("manually", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnAwsRejectionIsReportedWithTheCodeAwsGaveIt()
    {
        var control = Substitute.For<IAmazonBedrock>();
        control.ListFoundationModelsAsync(Arg.Any<ListFoundationModelsRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<ListFoundationModelsResponse>>(_ => throw new AmazonServiceException("not authorized")
            {
                ErrorCode = "AccessDeniedException",
                StatusCode = HttpStatusCode.Forbidden,
            });

        var result = await Driver(control: control).VerifyAsync(Endpoint());

        Assert.Equal(AiVerificationStatus.Failed, result.Status);
        Assert.Contains("AccessDeniedException", result.Summary, StringComparison.Ordinal);
    }

    // A model listing both output modalities can do both, and each carries its own wire shape. Keeping one would
    // leave the model unbindable for the other purpose, because a purpose is bound against what discovery
    // recorded.
    [Fact]
    public async Task AModelListingBothOutputModalitiesKeepsBothPurposesAndBothWireShapes()
    {
        var control = Substitute.For<IAmazonBedrock>();
        control.ListFoundationModelsAsync(Arg.Any<ListFoundationModelsRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new ListFoundationModelsResponse
                {
                    ModelSummaries = [Summary("amazon.nova-omni-v1:0", "Nova Omni", "Amazon", "TEXT", "EMBEDDING")],
                });

        var result = await Driver(control: control).DiscoverModelsAsync(Endpoint());

        var model = Assert.Single(result.Models);
        Assert.Contains(AiOperationKind.Chat, model.OperationKinds);
        Assert.Contains(AiOperationKind.Embedding, model.OperationKinds);
        Assert.Contains(BedrockProviderDriver.ConverseProtocol, model.SupportedProtocolModes);
        Assert.Contains(ProviderDeclaredProtocolModes.Embeddings, model.SupportedProtocolModes);
    }

    // An unreachable or egress-blocked endpoint is an ordinary misconfiguration. It never reaches AWS, so it
    // arrives as the transport's own exception rather than as the service exception the driver reads a status
    // off, and an operator who typed the wrong endpoint has to be told that rather than shown an internal error.
    [Theory]
    [MemberData(nameof(FailuresThatNeverReachedAws))]
    public async Task ACallThatNeverReachedAwsIsReportedRatherThanThrown(Exception failure)
    {
        var control = Substitute.For<IAmazonBedrock>();
        control.ListFoundationModelsAsync(Arg.Any<ListFoundationModelsRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<ListFoundationModelsResponse>>(_ => throw failure);

        var discovery = await Driver(control: control).DiscoverModelsAsync(Endpoint());
        Assert.Equal("failed", discovery.DiscoveryStatus);
        Assert.NotEmpty(discovery.Warnings);

        var verification = await Driver(control: control).VerifyAsync(Endpoint());
        Assert.Equal(AiVerificationStatus.Failed, verification.Status);
    }

    public static TheoryData<Exception> FailuresThatNeverReachedAws()
    {
        return
        [
            new HttpRequestException("Connection refused (bedrock.eu-central-1.amazonaws.com:443)"),
            new AmazonClientException("Unable to resolve AWS credentials."),
            new IOException("The response ended prematurely."),
        ];
    }

    // A secret that is not an access-key pair is a configuration mistake, and naming it beats a signing failure
    // from AWS that reads like a permissions problem. Discovery reads it the same way verification does: it
    // reaches the caller as a result, not as an exception the caller cannot place.
    [Fact]
    public async Task ASecretThatIsNotAnAccessKeyPairIsNamedByDiscoveryToo()
    {
        var factory = Substitute.For<IBedrockClientFactory>();
        factory.CreateControlPlaneClient(Arg.Any<ProviderEndpoint>())
            .Returns<IAmazonBedrock?>(_ => throw new InvalidOperationException(
                "An AWS Bedrock connection needs an access key. Store it as 'accessKeyId:secretAccessKey'."));

        var result = await new BedrockProviderDriver(factory).DiscoverModelsAsync(Endpoint());

        Assert.Equal("failed", result.DiscoveryStatus);
        Assert.Contains(result.Warnings, warning => warning.Contains("access key", StringComparison.OrdinalIgnoreCase));
    }

    // A secret that is not an access-key pair is a configuration mistake, and naming it beats a signing failure
    // from AWS that reads like a permissions problem.
    [Fact]
    public async Task ASecretThatIsNotAnAccessKeyPairIsNamedAsTheProblem()
    {
        var factory = Substitute.For<IBedrockClientFactory>();
        factory.CreateControlPlaneClient(Arg.Any<ProviderEndpoint>())
            .Returns<IAmazonBedrock?>(_ => throw new InvalidOperationException(
                "An AWS Bedrock connection needs an access key. Store it as 'accessKeyId:secretAccessKey'."));

        var result = await new BedrockProviderDriver(factory).VerifyAsync(Endpoint());

        Assert.Equal(AiVerificationStatus.Failed, result.Status);
        Assert.Contains("access key", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    // AWS reports throttling and capacity through its own exception type rather than as an HTTP failure, so the
    // shared rule cannot see them; retrying the first and giving up on the second is the whole point.
    [Theory]
    [InlineData("ThrottlingException", HttpStatusCode.BadRequest, true)]
    [InlineData("ModelNotReadyException", HttpStatusCode.BadRequest, true)]
    [InlineData("InternalServerException", HttpStatusCode.InternalServerError, true)]
    [InlineData("AccessDeniedException", HttpStatusCode.Forbidden, false)]
    [InlineData("ValidationException", HttpStatusCode.BadRequest, false)]
    public void AwsFailuresAreClassifiedByWhatAwsCalledThem(string errorCode, HttpStatusCode status, bool isTransient)
    {
        var verdict = Driver().ClassifyRuntimeFailure(new AmazonServiceException("rejected") { ErrorCode = errorCode, StatusCode = status });

        Assert.Equal(isTransient, verdict.IsTransient);
    }

    // Seam parity: the answer, its finish reason and its usage have to arrive in the same shape every other
    // provider produces, or everything downstream — cost, budget, the review itself — reads a different thing.
    [Fact]
    public async Task AnAnswerComesBackThroughTheSameSeamAsEveryOtherProvider()
    {
        var runtime = Substitute.For<IAmazonBedrockRuntime>();
        runtime.ConverseAsync(Arg.Any<ConverseRequest>(), Arg.Any<CancellationToken>())
            .Returns(TextAnswer("42"));

        using var client = Driver(runtime: runtime).CreateChatClient(Endpoint(), Model(), ProviderDeclaredProtocolModes.Auto);
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "what is 6*7?")]);

        Assert.Equal("42", response.Text);
        Assert.Equal(ChatFinishReason.Stop, response.FinishReason);
        Assert.Equal(11, response.Usage!.InputTokenCount);
        Assert.Equal(7, response.Usage.OutputTokenCount);
    }

    // Converse reports its input count exclusive of both cache buckets. Read as it stands, a call served largely
    // from cache prices its real prompt at nothing, because the host bills the input total less the buckets.
    [Fact]
    public async Task TheCacheBucketsConverseReportsAreAddedBackIntoTheInputTotal()
    {
        var runtime = Substitute.For<IAmazonBedrockRuntime>();
        var answer = TextAnswer("ok");
        answer.Usage.InputTokens = 120;
        answer.Usage.CacheReadInputTokens = 4000;
        answer.Usage.CacheWriteInputTokens = 50;
        runtime.ConverseAsync(Arg.Any<ConverseRequest>(), Arg.Any<CancellationToken>()).Returns(answer);

        var driver = Driver(runtime: runtime);
        using var client = driver.CreateChatClient(Endpoint(), Model(), ProviderDeclaredProtocolModes.Auto);
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        var usage = driver.ReadUsage(response.Usage);

        Assert.Equal(4170, usage.InputTokens);
        Assert.Equal(4000, usage.CachedInputTokens);
        Assert.Equal(50, usage.CacheWriteTokens);
        Assert.Equal(120, usage.NonCachedInputTokens);
        Assert.True(usage.CountersAreInclusive);
    }

    // The caller asks for reasoning in terms no provider owns; this driver is where that becomes Bedrock's own
    // extended-thinking configuration, which rides in the model-specific fields of the request.
    [Fact]
    public async Task AskingForReasoningReachesTheRequestAsExtendedThinking()
    {
        var runtime = Substitute.For<IAmazonBedrockRuntime>();
        runtime.ConverseAsync(Arg.Any<ConverseRequest>(), Arg.Any<CancellationToken>()).Returns(TextAnswer("ok"));

        using var client = Driver(runtime: runtime).CreateChatClient(Endpoint(), Model(), ProviderDeclaredProtocolModes.Auto);
        await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "hello")],
            new ChatOptions
            {
                MaxOutputTokens = 4000,
                RawRepresentationFactory = _ => new ProviderReasoningRequest(ProviderReasoningEffort.High, true),
            });

        var request = runtime.ReceivedCalls()
            .Select(call => call.GetArguments()[0])
            .OfType<ConverseRequest>()
            .Single();

        var thinking = request.AdditionalModelRequestFields;
        Assert.True(thinking.IsDictionary(), "extended thinking is carried in the model-specific request fields");
        Assert.Contains("thinking", thinking.AsDictionary().Keys);
    }

    // Caching is the model's property, not the provider's, and getting it wrong is not a missed optimisation: a
    // cache point sent to a model that cannot cache is a request Bedrock refuses outright.
    [Fact]
    public void CachingIsClaimedOnlyForAModelTheHostSaysCanCache()
    {
        var driver = Driver();

        Assert.True(driver.GetChatRuntimeCapabilities(Endpoint(), Model(caching: true), ProviderDeclaredProtocolModes.Auto).SupportsPromptCaching);
        Assert.False(driver.GetChatRuntimeCapabilities(Endpoint(), Model(), ProviderDeclaredProtocolModes.Auto).SupportsPromptCaching);
    }

    [Fact]
    public async Task AModelThatCannotCacheIsSentNoCachePointAtAll()
    {
        var request = await CapturedRequest(Model(), Conversation(LongText()));

        Assert.DoesNotContain(request.Messages.SelectMany(m => m.Content), block => block.CachePoint is not null);
        Assert.DoesNotContain(request.System ?? [], block => block.CachePoint is not null);
    }

    // The system turn is identical across every file of a review, so it is the block worth caching; the end of the
    // conversation is what a follow-up turn repeats.
    [Fact]
    public async Task ACachingModelGetsTheSystemTurnAndTheConversationEndMarked()
    {
        var request = await CapturedRequest(Model(caching: true), Conversation(LongText()));

        Assert.Contains(request.System ?? [], block => block.CachePoint is not null);
        Assert.NotNull(request.Messages[^1].Content.SingleOrDefault(block => block.CachePoint is not null));
    }

    // Below the floor the marker costs more to write than the reads save, so a short prompt is left unmarked even
    // on a model that could cache it.
    [Fact]
    public async Task APromptTooSmallToPayForItselfIsLeftUnmarked()
    {
        var request = await CapturedRequest(Model(caching: true), Conversation("still quite short"));

        Assert.DoesNotContain(request.Messages.SelectMany(m => m.Content), block => block.CachePoint is not null);
        Assert.DoesNotContain(request.System ?? [], block => block.CachePoint is not null);
    }

    // A multi-pass review hands the same conversation to several models. A Bedrock-specific marker left behind on
    // it would travel to a provider that cannot read it.
    [Fact]
    public async Task MarkingDoesNotWriteBackIntoTheCallersConversation()
    {
        var conversation = Conversation(LongText());

        await CapturedRequest(Model(caching: true), conversation);

        Assert.All(conversation, message => Assert.Null(message.AdditionalProperties));
    }

    // A family reaches the network only through the client factory the host supplies. Where the host supplied
    // none, the client refuses instead of falling back to a transport of the SDK's own, which would work in
    // every functional test while silently losing the connect-time address check.
    [Fact]
    public async Task AClientBuiltWithNoHostTransportRefusesRatherThanReachingTheNetworkItself()
    {
        using var client = new BedrockProviderDriver().CreateChatClient(Endpoint(), Model(), ProviderDeclaredProtocolModes.Auto);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));
    }

    private static async Task<ConverseRequest> CapturedRequest(
        ProviderModelDescriptor model,
        IReadOnlyList<ChatMessage> conversation)
    {
        var runtime = Substitute.For<IAmazonBedrockRuntime>();
        runtime.ConverseAsync(Arg.Any<ConverseRequest>(), Arg.Any<CancellationToken>()).Returns(TextAnswer("ok"));

        using var client = Driver(runtime: runtime).CreateChatClient(Endpoint(), model, ProviderDeclaredProtocolModes.Auto);
        await client.GetResponseAsync(conversation);

        return runtime.ReceivedCalls()
            .Select(call => call.GetArguments()[0])
            .OfType<ConverseRequest>()
            .Single();
    }

    private static List<ChatMessage> Conversation(string userText)
    {
        return
        [
            new ChatMessage(ChatRole.System, "You review code."),
            new ChatMessage(ChatRole.User, userText),
        ];
    }

    // Comfortably past the minimum-cacheable floor this family applies. Stated here rather than read from it: if
    // the floor ever rises above this the marking tests fail loudly rather than quietly asserting the wrong side
    // of it.
    private static string LongText()
    {
        return new string('x', 8192);
    }

    private static ProviderModelDescriptor Model(bool caching = false)
    {
        return new ProviderModelDescriptor(
            Guid.NewGuid(),
            "anthropic.claude-opus-4-5",
            [ProviderDeclaredProtocolModes.Auto, BedrockProviderDriver.ConverseProtocol],
            ReasoningContentField: null,
            SupportsPromptCaching: caching);
    }

    private static ProviderEndpoint Endpoint(string baseUrl = "https://bedrock-runtime.eu-central-1.amazonaws.com")
    {
        return new ProviderEndpoint("meisterdev/awsBedrock", baseUrl, BedrockProviderDriver.ApiKeyAuth, "ABSKQmVkcm9ja0FQSUtleS1FWEFNUExF");
    }

    private static AiProbeTarget Target(string baseUrl)
    {
        return new AiProbeTarget(baseUrl, BedrockProviderDriver.ApiKeyAuth, HasApiKey: true);
    }

    private static FoundationModelSummary Summary(
        string modelId,
        string modelName,
        string provider,
        params string[] outputModalities)
    {
        return new FoundationModelSummary
        {
            ModelId = modelId,
            ModelName = modelName,
            ProviderName = provider,
            OutputModalities = [.. outputModalities],
        };
    }

    private static ConverseResponse TextAnswer(string text)
    {
        return new ConverseResponse
        {
            Output = new ConverseOutput
            {
                Message = new Message
                {
                    Role = ConversationRole.Assistant,
                    Content = [new ContentBlock { Text = text }],
                },
            },
            StopReason = StopReason.End_turn,
            Usage = new TokenUsage { InputTokens = 11, OutputTokens = 7, TotalTokens = 18 },
        };
    }

    private static BedrockProviderDriver Driver(
        IAmazonBedrock? control = null,
        IAmazonBedrockRuntime? runtime = null)
    {
        var factory = Substitute.For<IBedrockClientFactory>();
        factory.CreateControlPlaneClient(Arg.Any<ProviderEndpoint>()).Returns(control);
        factory.CreateRuntimeClient(Arg.Any<ProviderEndpoint>()).Returns(runtime ?? Substitute.For<IAmazonBedrockRuntime>());

        return new BedrockProviderDriver(factory);
    }
}
