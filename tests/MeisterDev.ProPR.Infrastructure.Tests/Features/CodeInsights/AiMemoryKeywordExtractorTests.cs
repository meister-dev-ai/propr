// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Classification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.CodeInsights;

public sealed class AiMemoryKeywordExtractorTests
{
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private const string Summary = "The null check was added and the anonymous-caller path now returns 401.";

    [Fact]
    public async Task ExtractAsync_ReturnsTheKeywordsLowerCased()
    {
        var sut = CreateExtractor("""["Null-Check","Authentication","http.401"]""");

        var keywords = await sut.ExtractAsync(ClientId, Summary, null);

        Assert.Equal(["null-check", "authentication", "http.401"], keywords);
    }

    [Fact]
    public async Task ExtractAsync_ToleratesProseAroundTheArray()
    {
        var sut = CreateExtractor("""Here are the keywords: ["null-check","authentication"]: hope that helps.""");

        var keywords = await sut.ExtractAsync(ClientId, Summary, null);

        Assert.Equal(["null-check", "authentication"], keywords);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("""{"keywords":"null-check"}""")]
    public async Task ExtractAsync_AnUnusableResponseYieldsNoKeywords(string body)
    {
        var sut = CreateExtractor(body);

        Assert.Empty(await sut.ExtractAsync(ClientId, Summary, null));
    }

    [Fact]
    public async Task ExtractAsync_RecoversTheArrayWhenTheModelWrapsItInAnObject()
    {
        // Lenient on shape, strict on content: the values still go through sanitisation, so accepting a
        // wrapped array costs nothing and salvages an otherwise-wasted call.
        var sut = CreateExtractor("""{"keywords":["null-check","authentication"]}""");

        Assert.Equal(["null-check", "authentication"], await sut.ExtractAsync(ClientId, Summary, null));
    }

    [Fact]
    public async Task ExtractAsync_WithNoSummary_MakesNoCallAtAll()
    {
        var resolver = Substitute.For<IAiRuntimeResolver>();
        var sut = new AiMemoryKeywordExtractor(resolver, NullLogger<AiMemoryKeywordExtractor>.Instance);

        Assert.Empty(await sut.ExtractAsync(ClientId, "   ", null));

        await resolver.DidNotReceive()
            .ResolveChatRuntimeAsync(Arg.Any<Guid>(), Arg.Any<AiPurpose>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractAsync_WithNoModelBound_YieldsNoKeywordsAndDoesNotThrow()
    {
        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeAsync(ClientId, AiPurpose.InsightsClassification, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AiPurposeBindingNotConfiguredException(AiPurpose.InsightsClassification));

        var sut = new AiMemoryKeywordExtractor(resolver, NullLogger<AiMemoryKeywordExtractor>.Instance);

        Assert.Empty(await sut.ExtractAsync(ClientId, Summary, null));
    }

    [Fact]
    public async Task ExtractAsync_WhenTheCallFails_YieldsNoKeywordsAndDoesNotThrow()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("the provider is down"));

        var sut = new AiMemoryKeywordExtractor(
            CreateResolver(chatClient),
            NullLogger<AiMemoryKeywordExtractor>.Instance);

        Assert.Empty(await sut.ExtractAsync(ClientId, Summary, null));
    }

    [Fact]
    public void Sanitize_CapsTheCount()
    {
        // A long list is not a search aid, it is noise.
        var many = Enumerable.Range(0, 50).Select(index => $"keyword{index}");

        var keywords = AiMemoryKeywordExtractor.Sanitize(many);

        Assert.Equal(AiMemoryKeywordExtractor.MaxKeywords, keywords.Count);
    }

    [Fact]
    public void Sanitize_DropsAnythingThatIsNotAPlainKeyword()
    {
        // A keyword is displayed, so anything odd reaching one would be a visible leak rather than merely a
        // stored one. Dropped rather than shortened: a truncated keyword looks deliberate.
        var candidates = new[]
        {
            "null-check",
            "ghp_averyrealisticlookingsecrettokenvaluehere1234567890",
            "has space",
            "quote\"inside",
            new string('x', AiMemoryKeywordExtractor.MaxKeywordLength + 1),
            "  padded  ",
            string.Empty,
        };

        var keywords = AiMemoryKeywordExtractor.Sanitize(candidates);

        Assert.Equal(["null-check", "padded"], keywords);
    }

    [Fact]
    public void Sanitize_DeduplicatesCaseInsensitivelyByLowerCasingFirst()
    {
        var keywords = AiMemoryKeywordExtractor.Sanitize(["Null-Check", "null-check", "NULL-CHECK"]);

        Assert.Single(keywords);
    }

    [Fact]
    public async Task ExtractAsync_TellsTheModelToAvoidGenericReviewVocabulary()
    {
        var captured = new List<ChatMessage>();
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(messages => captured.AddRange(messages)),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, """["null-check"]""")));

        var sut = new AiMemoryKeywordExtractor(
            CreateResolver(chatClient),
            NullLogger<AiMemoryKeywordExtractor>.Instance);

        await sut.ExtractAsync(ClientId, Summary, "@@ -1 +1 @@");

        var system = captured.First(message => message.Role == ChatRole.System).Text;
        var user = captured.First(message => message.Role == ChatRole.User).Text;

        // Every memory would match "code" or "review", which makes such a keyword worse than none.
        Assert.Contains("Avoid generic", system, StringComparison.Ordinal);
        Assert.Contains(Summary, user, StringComparison.Ordinal);
    }

    private static AiMemoryKeywordExtractor CreateExtractor(string responseText)
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));

        return new AiMemoryKeywordExtractor(
            CreateResolver(chatClient),
            NullLogger<AiMemoryKeywordExtractor>.Instance);
    }

    private static IAiRuntimeResolver CreateResolver(IChatClient chatClient)
    {
        var runtime = Substitute.For<IResolvedAiChatRuntime>();
        runtime.ChatClient.Returns(chatClient);

        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeAsync(ClientId, AiPurpose.InsightsClassification, Arg.Any<CancellationToken>())
            .Returns(runtime);
        return resolver;
    }
}
