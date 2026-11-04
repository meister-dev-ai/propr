// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Tests.AI;

/// <summary>The catalog models a discovery offers beside the models the provider listed.</summary>
public sealed class CatalogModelSuggestionsTests
{
    private static readonly IReadOnlyList<string> ChatModes = [ProviderDeclaredProtocolModes.Auto, "meisterdev/openAi:ChatCompletions"];

    private static readonly IReadOnlyList<AiModelCatalogEntryDto> Catalog =
    [
        Entry("openai", "gpt-4o", input: 2.5m),
        Entry("openai", "text-embedding-3-large", family: "text-embedding", context: 8191),
        Entry("azure", "gpt-4o", input: 2.75m),
        Entry("azure", "cohere-embed-v3-english", family: "cohere-embed", context: 512),
        Entry("anthropic", "claude-sonnet-4-5"),
        Entry("google-vertex", "gemini-2.5-pro"),
        Entry("amazon-bedrock", "anthropic.claude-sonnet-4-5-v1:0"),
        Entry("mistral", "mistral-embed", family: "mistral-embed", context: 8000),
        Entry("mistral", "mistral-large-latest"),
    ];

    [Theory]
    [InlineData("meisterdev/openAi", new[] { "text-embedding-3-large", "gpt-4o" })]
    [InlineData("meisterdev/azureOpenAi", new[] { "cohere-embed-v3-english", "gpt-4o" })]
    [InlineData("meisterdev/anthropic", new[] { "claude-sonnet-4-5" })]
    [InlineData("meisterdev/googleVertex", new[] { "gemini-2.5-pro" })]
    [InlineData("meisterdev/awsBedrock", new[] { "anthropic.claude-sonnet-4-5-v1:0" })]
    public void AVendorFamilyIsOfferedTheModelsOfItsOwnCatalogProvider(string providerKind, string[] expected)
    {
        var offered = CatalogModelSuggestions.Suggest(providerKind, Catalog, [], ChatModes);

        Assert.Equal(expected, offered.Select(model => model.RemoteModelId));
    }

    [Theory]
    [InlineData("meisterdev/openAiCompatible")]
    [InlineData("meisterdev/liteLlm")]
    public void AGatewayFamilyIsOfferedTheWholeCatalogWithEachIdentifierOnce(string providerKind)
    {
        var offered = CatalogModelSuggestions.Suggest(providerKind, Catalog, [], ChatModes);

        Assert.Equal(8, offered.Count);
        Assert.Single(offered, model => model.RemoteModelId == "gpt-4o");
        Assert.Equal(
            ["cohere-embed-v3-english", "mistral-embed", "text-embedding-3-large"],
            offered.Where(model => model.SupportsEmbedding).Select(model => model.RemoteModelId).Order(StringComparer.Ordinal));
    }

    // The price of a model sold by several catalog providers at different rates is unknown for a gateway.
    [Fact]
    public void AModelSeveralProvidersSellAtDifferentRatesIsOfferedWithoutAPrice()
    {
        var offered = CatalogModelSuggestions.Suggest("meisterdev/openAiCompatible", Catalog, [], ChatModes);

        Assert.Null(Assert.Single(offered, model => model.RemoteModelId == "gpt-4o").InputCostPer1MUsd);
        Assert.Equal(
            2.5m,
            Assert.Single(CatalogModelSuggestions.Suggest("meisterdev/openAi", Catalog, [], ChatModes), model => model.RemoteModelId == "gpt-4o")
                .InputCostPer1MUsd);
    }

    [Fact]
    public void AFamilyWithoutACatalogProviderIsOfferedNothing()
    {
        Assert.Empty(CatalogModelSuggestions.Suggest("acme/private-family", Catalog, [], ChatModes));
    }

    [Fact]
    public void AModelTheProviderListedIsNotOfferedAgain()
    {
        var offered = CatalogModelSuggestions.Suggest("meisterdev/openAi", Catalog, ["GPT-4o "], ChatModes);

        Assert.Equal(["text-embedding-3-large"], offered.Select(model => model.RemoteModelId));
    }

    [Theory]
    [InlineData("gpt-image-1", null)]
    [InlineData("chatgpt-latest", "gpt-image")]
    [InlineData("gpt-realtime-2.1", null)]
    [InlineData("gpt-4o-audio-preview", null)]
    [InlineData("gpt-4o-mini-tts", null)]
    [InlineData("gpt-4o-transcribe", null)]
    [InlineData("omni-moderation-latest", null)]
    [InlineData("whisper-large-v3", null)]
    [InlineData("veo-3.1-generate-preview", "video")]
    public void AModelForImagesSpeechRealtimeOrModerationIsNotOffered(string remoteModelId, string? family)
    {
        var offered = CatalogModelSuggestions.Suggest(
            "meisterdev/openAi",
            [Entry("openai", remoteModelId, family: family), Entry("openai", "gpt-4o")],
            [],
            ChatModes);

        Assert.Equal(["gpt-4o"], offered.Select(model => model.RemoteModelId));
    }

    [Fact]
    public void AnEmbeddingEntryIsOfferedAsAnEmbeddingModelWithTheDefaults()
    {
        var offered = CatalogModelSuggestions.Suggest("meisterdev/openAiCompatible", Catalog, [], ChatModes);

        var documented = Assert.Single(offered, model => model.RemoteModelId == "text-embedding-3-large");
        Assert.Equal([AiOperationKind.Embedding], documented.OperationKinds);
        Assert.Equal([ProviderDeclaredProtocolModes.Auto, ProviderDeclaredProtocolModes.Embeddings], documented.SupportedProtocolModes);
        Assert.Equal(("cl100k_base", (int?)8191, (int?)3072), (documented.TokenizerName, documented.MaxInputTokens, documented.EmbeddingDimensions));

        // The family names it as an embedding model, and the catalog context is its input limit.
        var mistral = Assert.Single(offered, model => model.RemoteModelId == "mistral-embed");
        Assert.Equal(("cl100k_base", (int?)8000, (int?)1536), (mistral.TokenizerName, mistral.MaxInputTokens, mistral.EmbeddingDimensions));
    }

    [Fact]
    public void AChatEntryIsOfferedAsAChatModelWithTheCatalogFacts()
    {
        var offered = CatalogModelSuggestions.Suggest(
            "meisterdev/openAi",
            [Entry("openai", "gpt-4o", input: 2.5m, context: 128_000) with { SupportsToolUse = true, DisplayName = "GPT-4o" }],
            [],
            ChatModes);

        var model = Assert.Single(offered);
        Assert.Equal([AiOperationKind.Chat], model.OperationKinds);
        Assert.Equal(ChatModes, model.SupportedProtocolModes);
        Assert.Equal(
            ("GPT-4o", (int?)128_000, (decimal?)2.5m, true), (model.DisplayName, model.MaxContextTokens, model.InputCostPer1MUsd, model.SupportsToolUse));
        Assert.Null(model.EmbeddingDimensions);
    }

    [Fact]
    public void EveryOfferedModelIsMarkedAsComingFromTheCatalog()
    {
        var offered = CatalogModelSuggestions.Suggest("meisterdev/openAiCompatible", Catalog, [], ChatModes);

        Assert.All(offered, model => Assert.Equal(AiConfiguredModelSource.KnownCatalog, model.Source));
        Assert.All(offered, model => Assert.Equal(Guid.Empty, model.Id));
    }

    // A gateway is offered the whole catalog, which holds several hundred models, so the list is bounded. The
    // embedding models come first, so the bound never removes them, and the newest chat models follow.
    [Fact]
    public void TheListIsBoundedWithTheEmbeddingModelsFirstAndTheNewestChatModelsNext()
    {
        var catalog = Enumerable.Range(0, 300)
            .Select(index => Entry("openrouter", $"chat-{index:D3}", released: new DateOnly(2024, 1, 1).AddDays(index)))
            .Append(Entry("mistral", "mistral-embed", family: "mistral-embed", released: new DateOnly(2020, 1, 1)))
            .ToList();

        var offered = CatalogModelSuggestions.Suggest("meisterdev/openAiCompatible", catalog, [], ChatModes);

        Assert.Equal(CatalogModelSuggestions.MaximumModels, offered.Count);
        Assert.Equal("mistral-embed", offered[0].RemoteModelId);
        Assert.Equal("chat-299", offered[1].RemoteModelId);
        Assert.Equal("chat-101", offered[^1].RemoteModelId);
    }

    private static AiModelCatalogEntryDto Entry(
        string providerId,
        string remoteModelId,
        string? family = null,
        int? context = null,
        decimal? input = null,
        DateOnly? released = null)
    {
        return new AiModelCatalogEntryDto(
            providerId, providerId, remoteModelId, remoteModelId, family, false, false, false, false, null, context, null,
            input, null, null, null, false, released, AiModelCatalogLayer.Global);
    }
}
