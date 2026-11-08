// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Application.Tests.AI;

public sealed class ConfiguredModelReasoningLevelsTests
{
    private const string OpenAiKind = "meisterdev/openAi";
    private const string LiteLlmKind = "meisterdev/liteLlm";

    private static readonly IReadOnlySet<string> NothingAgain = new HashSet<string>();

    [Fact]
    public void FromCatalog_ReadsTheEntryOfTheFamilysOwnCatalogProvider()
    {
        IReadOnlyList<AiModelCatalogEntryDto> catalog =
        [
            Entry("openrouter", "o3", ["low", "medium", "high", "xhigh"]),
            Entry("openai", "o3", ["low", "medium", "high"]),
        ];

        Assert.Equal(["low", "medium", "high"], ConfiguredModelReasoningLevels.FromCatalog(OpenAiKind, "o3", catalog));
    }

    // A gateway can route the model to any provider that lists it, so only the levels all of them accept are safe.
    [Fact]
    public void FromCatalog_KeepsTheSharedLevels_ForAFamilyThatServesTheWholeCatalog()
    {
        IReadOnlyList<AiModelCatalogEntryDto> catalog =
        [
            Entry("deepinfra", "gpt-oss-120b", ["low", "medium", "high"]),
            Entry("groq", "gpt-oss-120b", ["medium", "high"]),
        ];

        Assert.Equal(["medium", "high"], ConfiguredModelReasoningLevels.FromCatalog(LiteLlmKind, "gpt-oss-120b", catalog));
    }

    [Fact]
    public void FromCatalog_ReturnsNull_WhenNoEntryStatesLevels()
    {
        IReadOnlyList<AiModelCatalogEntryDto> catalog = [Entry("openai", "gpt-4o", null)];

        Assert.Null(ConfiguredModelReasoningLevels.FromCatalog(OpenAiKind, "gpt-4o", catalog));
    }

    [Fact]
    public void Detect_PrefersTheLevelsTheProviderReported()
    {
        var (levels, source) = ConfiguredModelReasoningLevels.Detect(
            null,
            OpenAiKind,
            "o3",
            [Entry("openai", "o3", ["low", "medium", "high"])],
            ["low", "high"]);

        Assert.Equal(["low", "high"], levels);
        Assert.Equal(AiReasoningLevelSource.Provider, source);
    }

    [Fact]
    public void Detect_UsesTheCatalog_WhenTheAddInKeepsItsLevels()
    {
        var driver = PassThroughDriver();

        var (levels, source) = ConfiguredModelReasoningLevels.Detect(driver, OpenAiKind, "o3", [Entry("openai", "o3", ["low", "medium", "high"])]);

        Assert.Equal(["low", "medium", "high"], levels);
        Assert.Equal(AiReasoningLevelSource.Catalog, source);
    }

    [Fact]
    public void Detect_ReportsTheAddIn_WhenTheAddInCompletesTheCatalog()
    {
        var driver = Substitute.For<IAiProviderDriver>();
        driver.DescribeReasoningLevels("claude-opus-5", Arg.Any<IReadOnlyList<string>?>()).Returns(["none", "low", "max"]);

        var (levels, source) = ConfiguredModelReasoningLevels.Detect(
            driver,
            "meisterdev/anthropic",
            "claude-opus-5",
            [Entry("anthropic", "claude-opus-5", ["low", "max"])]);

        Assert.Equal(["none", "low", "max"], levels);
        Assert.Equal(AiReasoningLevelSource.AddIn, source);
    }

    [Fact]
    public void Detect_FallsBackToTheCatalog_WhenTheAddInThrows()
    {
        var driver = Substitute.For<IAiProviderDriver>();
        driver.DescribeReasoningLevels(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>?>()).Throws(new InvalidOperationException("broken add-in"));

        var (levels, source) = ConfiguredModelReasoningLevels.Detect(driver, OpenAiKind, "o3", [Entry("openai", "o3", ["low", "high"])]);

        Assert.Equal(["low", "high"], levels);
        Assert.Equal(AiReasoningLevelSource.Catalog, source);
    }

    [Fact]
    public void Detect_ReturnsNoLevels_WhenNoSourceKnowsTheModel()
    {
        var (levels, source) = ConfiguredModelReasoningLevels.Detect(PassThroughDriver(), OpenAiKind, "private-model", []);

        Assert.Null(levels);
        Assert.Null(source);
    }

    [Fact]
    public void Complete_KeepsTheLevelsAModelStates()
    {
        var model = Chat("o3") with { ReasoningLevels = ["high"], ReasoningLevelsSource = AiReasoningLevelSource.Operator };

        var completed = Assert.Single(Complete([model], stored: null));

        Assert.Equal(["high"], completed.ReasoningLevels);
        Assert.Equal(AiReasoningLevelSource.Operator, completed.ReasoningLevelsSource);
    }

    // A client that copies the saved fields of a model, or sends no levels at all, keeps what was stored.
    [Fact]
    public void Complete_KeepsTheStoredLevels_OfASavedModelThatStatesNone()
    {
        var stored = Chat("o3") with { Id = Guid.NewGuid(), ReasoningLevels = [], ReasoningLevelsSource = AiReasoningLevelSource.Operator };

        var completed = Assert.Single(Complete([Chat("o3") with { Id = stored.Id }], [stored]));

        Assert.Empty(completed.ReasoningLevels!);
        Assert.Equal(AiReasoningLevelSource.Operator, completed.ReasoningLevelsSource);
    }

    [Fact]
    public void Complete_DetectsTheLevels_OfANewModelThatStatesNone()
    {
        var completed = Assert.Single(Complete([Chat("o3")], stored: null));

        Assert.Equal(["low", "medium", "high"], completed.ReasoningLevels);
        Assert.Equal(AiReasoningLevelSource.Catalog, completed.ReasoningLevelsSource);
    }

    [Fact]
    public void Complete_ReplacesStatedAndStoredLevels_WhenTheRequestAsksToDetectThemAgain()
    {
        var stored = Chat("o3") with { ReasoningLevels = ["max"], ReasoningLevelsSource = AiReasoningLevelSource.Operator };
        var model = Chat("o3") with { ReasoningLevels = ["max"], ReasoningLevelsSource = AiReasoningLevelSource.Operator };

        var completed = Assert.Single(Complete([model], [stored], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "O3" }));

        Assert.Equal(["low", "medium", "high"], completed.ReasoningLevels);
        Assert.Equal(AiReasoningLevelSource.Catalog, completed.ReasoningLevelsSource);
    }

    // A default level the operator chose states that the model accepts it, so a detected list that lacks it gains it.
    [Fact]
    public void Complete_AddsTheDefaultLevel_ToDetectedLevelsThatLackIt()
    {
        var completed = Assert.Single(Complete([Chat("o3") with { DefaultReasoningLevel = "none" }], stored: null));

        Assert.Equal(["none", "low", "medium", "high"], completed.ReasoningLevels);
        Assert.Equal(AiReasoningLevelSource.Catalog, completed.ReasoningLevelsSource);
    }

    // A list made of the default level alone would send every other configured level as that level or as none.
    [Fact]
    public void Complete_LeavesTheLevelsUnknown_WhenNoSourceKnowsAModelWithADefaultLevel()
    {
        var completed = Assert.Single(Complete([Chat("private-model") with { DefaultReasoningLevel = "none" }], stored: null));

        Assert.Null(completed.ReasoningLevels);
        Assert.Null(completed.ReasoningLevelsSource);
        Assert.Equal("none", completed.DefaultReasoningLevel);
    }

    [Fact]
    public void Complete_LeavesAnEmbeddingModelAlone()
    {
        var embedding = Chat("o3") with { OperationKinds = [AiOperationKind.Embedding] };

        var completed = Assert.Single(Complete([embedding], stored: null));

        Assert.Null(completed.ReasoningLevels);
    }

    [Fact]
    public void Refusal_NamesALevelThatIsNotALevelName()
    {
        var refusal = ConfiguredModelReasoningLevels.Refusal("o3", ["low", "very high"]);

        Assert.Contains("'very high'", refusal, StringComparison.Ordinal);
        Assert.Null(ConfiguredModelReasoningLevels.Refusal("o3", [" Low ", "xhigh"]));
        Assert.Null(ConfiguredModelReasoningLevels.Refusal("o3", null));
    }

    [Fact]
    public void Refusal_RefusesMoreLevelsThanAModelLists()
    {
        var refusal = ConfiguredModelReasoningLevels.Refusal("o3", [.. Enumerable.Range(0, 17).Select(index => $"level{index}")]);

        Assert.Contains("at most 16", refusal, StringComparison.Ordinal);
    }

    private static IReadOnlyList<AiConfiguredModelDto> Complete(
        IReadOnlyList<AiConfiguredModelDto> models,
        IReadOnlyList<AiConfiguredModelDto>? stored,
        IReadOnlySet<string>? again = null)
    {
        return ConfiguredModelReasoningLevels.Complete(
            models,
            stored,
            again ?? NothingAgain,
            PassThroughDriver(),
            OpenAiKind,
            [Entry("openai", "o3", ["low", "medium", "high"])]);
    }

    // A family that keeps the catalog's levels, which is what the default member of the contract does.
    private static IAiProviderDriver PassThroughDriver()
    {
        var driver = Substitute.For<IAiProviderDriver>();
        driver.DescribeReasoningLevels(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(call => call.ArgAt<IReadOnlyList<string>?>(1));
        return driver;
    }

    private static AiConfiguredModelDto Chat(string remoteModelId)
    {
        return new AiConfiguredModelDto(
            Guid.Empty,
            remoteModelId,
            remoteModelId,
            [AiOperationKind.Chat],
            [ProviderDeclaredProtocolModes.Auto]);
    }

    private static AiModelCatalogEntryDto Entry(string providerId, string remoteModelId, IReadOnlyList<string>? levels)
    {
        return new AiModelCatalogEntryDto(
            providerId,
            providerId,
            remoteModelId,
            remoteModelId,
            null,
            true,
            true,
            true,
            false,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            null,
            AiModelCatalogLayer.Global,
            levels);
    }
}
