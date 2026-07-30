// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Features.CodeInsights.Ports;
using MeisterDev.ProPR.Application.Features.CodeInsights.Taxonomy;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Classification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.CodeInsights;

public sealed class AiFindingTypeClassifierTests
{
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FindingId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CustomTagId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task ClassifyAsync_ReturnsTheTypesLevelQualifierAndConfidence()
    {
        var sut = CreateClassifier("""{"types":["data-validation","security"],"level":"member","qualifier":"missing","confidence":0.82}""");

        var verdict = (await sut.ClassifyAsync(CreateRequest())).Verdict;

        Assert.NotNull(verdict);
        Assert.Equal(["data-validation", "security"], verdict.CoreSlugs);
        Assert.Empty(verdict.CustomTagIds);
        Assert.Equal(CodeInsightFindingLevel.Member, verdict.Level);
        Assert.Equal(CodeInsightFindingQualifier.Missing, verdict.Qualifier);
        Assert.Equal(0.82, verdict.Confidence, 3);
    }

    [Fact]
    public async Task ClassifyAsync_ResolvesACustomTagToItsIdentityNotItsName()
    {
        // Assignments must reference a tag's identity so renaming it never relabels history.
        var sut = CreateClassifier("""{"types":["design-structure","domain-rule"],"level":"type","qualifier":"incorrect","confidence":0.6}""");

        var verdict = (await sut.ClassifyAsync(CreateRequest())).Verdict;

        Assert.NotNull(verdict);
        Assert.Equal(["design-structure"], verdict.CoreSlugs);
        Assert.Equal([CustomTagId], verdict.CustomTagIds);
    }

    [Fact]
    public async Task ClassifyAsync_DropsATypeThatIsNotInTheSuppliedVocabulary()
    {
        // A label nothing defines cannot be aggregated, compared, or explained to whoever reads the chart.
        var sut = CreateClassifier("""{"types":["logic-error","spaghetti-code"],"level":"member","qualifier":"incorrect","confidence":0.9}""");

        var verdict = (await sut.ClassifyAsync(CreateRequest())).Verdict;

        Assert.NotNull(verdict);
        Assert.Equal(["logic-error"], verdict.CoreSlugs);
        Assert.Empty(verdict.CustomTagIds);
    }

    [Fact]
    public async Task ClassifyAsync_WithNoInVocabularyCoreType_ReportsNothingUsable()
    {
        // A finding with only an invented or only a custom type would be invisible to every cross-client view,
        // so it counts as unclassified and gets retried rather than being stored half-labelled.
        var sut = CreateClassifier("""{"types":["invented","domain-rule"],"level":"file","qualifier":"missing","confidence":0.5}""");

        Assert.Null((await sut.ClassifyAsync(CreateRequest())).Verdict);
    }

    [Theory]
    [InlineData("statement", CodeInsightFindingLevel.Statement)]
    [InlineData("member", CodeInsightFindingLevel.Member)]
    [InlineData("type", CodeInsightFindingLevel.Type)]
    [InlineData("file", CodeInsightFindingLevel.File)]
    [InlineData("crossFile", CodeInsightFindingLevel.CrossFile)]
    [InlineData("CROSSFILE", CodeInsightFindingLevel.CrossFile)]
    public async Task ClassifyAsync_ReadsEveryLevelRegardlessOfCase(string raw, CodeInsightFindingLevel expected)
    {
        var sut = CreateClassifier($$"""{"types":["logic-error"],"level":"{{raw}}","qualifier":"incorrect","confidence":0.7}""");

        var verdict = (await sut.ClassifyAsync(CreateRequest())).Verdict;

        Assert.Equal(expected, verdict!.Level);
    }

    [Fact]
    public async Task ClassifyAsync_AnUnreadableLevelDefaultsToTheNarrowestClaim()
    {
        // Over-stating blast radius would inflate exactly the number an operator would act on.
        var sut = CreateClassifier("""{"types":["logic-error"],"level":"galaxy","qualifier":"incorrect","confidence":0.7}""");

        var verdict = (await sut.ClassifyAsync(CreateRequest())).Verdict;

        Assert.Equal(CodeInsightFindingLevel.Statement, verdict!.Level);
    }

    [Theory]
    [InlineData("""{"types":["logic-error"],"level":"member","confidence":0.5}""")]
    [InlineData("""{"types":["logic-error"],"level":"member","qualifier":"nonsense","confidence":0.5}""")]
    public async Task ClassifyAsync_AnUnreadableQualifierDefaultsToIncorrect(string body)
    {
        var sut = CreateClassifier(body);

        var verdict = (await sut.ClassifyAsync(CreateRequest())).Verdict;

        Assert.Equal(CodeInsightFindingQualifier.Incorrect, verdict!.Qualifier);
    }

    [Theory]
    [InlineData("""{"types":["logic-error"],"level":"member","qualifier":"missing","confidence":5}""", 1d)]
    [InlineData("""{"types":["logic-error"],"level":"member","qualifier":"missing","confidence":-2}""", 0d)]
    [InlineData("""{"types":["logic-error"],"level":"member","qualifier":"missing"}""", 0d)]
    public async Task ClassifyAsync_ClampsConfidenceIntoRange(string body, double expected)
    {
        var sut = CreateClassifier(body);

        var verdict = (await sut.ClassifyAsync(CreateRequest())).Verdict;

        Assert.Equal(expected, verdict!.Confidence);
    }

    [Fact]
    public async Task ClassifyAsync_ToleratesProseAroundTheJson()
    {
        var sut = CreateClassifier(
            """Sure! Here you go: {"types":["performance"],"level":"member","qualifier":"incorrect","confidence":0.4} Hope that helps.""");

        var verdict = (await sut.ClassifyAsync(CreateRequest())).Verdict;

        Assert.Equal(["performance"], verdict!.CoreSlugs);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("""{"types":"logic-error"}""")]
    [InlineData("""["logic-error"]""")]
    public async Task ClassifyAsync_AnUnusableResponseReportsNothing(string body)
    {
        var sut = CreateClassifier(body);

        Assert.Null((await sut.ClassifyAsync(CreateRequest())).Verdict);
    }

    [Fact]
    public async Task ClassifyAsync_WithNoModelBound_ReportsNothingAndDoesNotThrow()
    {
        var resolver = Substitute.For<IAiRuntimeResolver>();
        resolver.ResolveChatRuntimeAsync(ClientId, AiPurpose.InsightsClassification, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AiPurposeBindingNotConfiguredException(AiPurpose.InsightsClassification));

        var sut = new AiFindingTypeClassifier(resolver, NullLogger<AiFindingTypeClassifier>.Instance);

        Assert.Null((await sut.ClassifyAsync(CreateRequest())).Verdict);
    }

    [Fact]
    public async Task ClassifyAsync_WhenTheCallFails_ReportsNothingAndDoesNotThrow()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("the provider is down"));

        var sut = new AiFindingTypeClassifier(
            CreateResolver(chatClient),
            NullLogger<AiFindingTypeClassifier>.Instance);

        Assert.Null((await sut.ClassifyAsync(CreateRequest())).Verdict);
    }

    [Fact]
    public async Task ClassifyAsync_PromptCarriesEachTypesDefinitionAndTheAnchorContext()
    {
        var captured = new List<ChatMessage>();
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(messages => captured.AddRange(messages)),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        """{"types":["logic-error"],"level":"member","qualifier":"incorrect","confidence":0.7}""")));

        var sut = new AiFindingTypeClassifier(
            CreateResolver(chatClient),
            NullLogger<AiFindingTypeClassifier>.Instance);

        await sut.ClassifyAsync(CreateRequest());

        var system = captured.First(message => message.Role == ChatRole.System).Text;
        var user = captured.First(message => message.Role == ChatRole.User).Text;

        // The definition an operator reads is the definition the model classifies against, one string.
        Assert.Contains("logic-error: Wrong control flow", system, StringComparison.Ordinal);
        Assert.Contains("domain-rule: Violates one of our business rules.", system, StringComparison.Ordinal);
        // The producing pass is the main signal available for the level axis, so it must reach the model.
        Assert.Contains("PrWide", user, StringComparison.Ordinal);
        Assert.Contains("src/Service.cs:42", user, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClassifyAsync_BoundsTheFindingTextSoOnePathologicalFindingCannotDominateCost()
    {
        var captured = new List<ChatMessage>();
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(messages => captured.AddRange(messages)),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        """{"types":["logic-error"],"level":"member","qualifier":"incorrect","confidence":0.7}""")));

        var sut = new AiFindingTypeClassifier(
            CreateResolver(chatClient),
            NullLogger<AiFindingTypeClassifier>.Instance);

        var request = CreateRequest() with { Message = new string('x', 20_000) };
        await sut.ClassifyAsync(request);

        var user = captured.First(message => message.Role == ChatRole.User).Text;
        Assert.Contains("(truncated)", user, StringComparison.Ordinal);
        Assert.True(user.Length < 6_000, $"The prompt should be bounded, was {user.Length} chars.");
    }

    [Fact]
    public void ClassifierVersion_IsStableAndNonEmpty()
    {
        // Stamped onto every assignment, so a re-grade can tell one prompt generation from another.
        var sut = new AiFindingTypeClassifier(
            Substitute.For<IAiRuntimeResolver>(),
            NullLogger<AiFindingTypeClassifier>.Instance);

        Assert.False(string.IsNullOrWhiteSpace(sut.ClassifierVersion));
    }

    private static AiFindingTypeClassifier CreateClassifier(string responseText)
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));

        return new AiFindingTypeClassifier(
            CreateResolver(chatClient),
            NullLogger<AiFindingTypeClassifier>.Instance);
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

    private static FindingClassificationRequest CreateRequest()
    {
        return new FindingClassificationRequest(
            ClientId,
            FindingId,
            "The null check on `user` is missing before it is dereferenced.",
            "src/Service.cs",
            42,
            CommentSeverity.Error,
            "PrWide",
            CreateVocabulary());
    }

    private static CodeInsightTaxonomyDto CreateVocabulary()
    {
        return new CodeInsightTaxonomyDto(
            CodeInsightCoreTaxonomy.Version,
            CodeInsightCoreTaxonomy.All
                .Select(tag => new CodeInsightCoreTagDto(
                    tag.Slug,
                    tag.DisplayName,
                    tag.Definition,
                    tag.Characteristic,
                    tag.BehaviourChanging))
                .ToList(),
            [
                new CodeInsightCustomTagDto(
                    CustomTagId,
                    "domain-rule",
                    "Domain rule",
                    "Violates one of our business rules.",
                    null,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
            ]);
    }
}
