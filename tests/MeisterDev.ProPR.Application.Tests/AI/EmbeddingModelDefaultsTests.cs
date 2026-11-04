// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;

namespace MeisterDev.ProPR.Application.Tests.AI;

/// <summary>The values ProPR fills in for an embedding model whose configuration leaves them out.</summary>
public sealed class EmbeddingModelDefaultsTests
{
    [Theory]
    [InlineData("text-embedding-3-large", 3072)]
    [InlineData("openai/text-embedding-3-large", 3072)]
    [InlineData("azure/text-embedding-3-small", 1536)]
    public void AModelInTheTableGetsItsDocumentedValues(string remoteModelId, int dimensions)
    {
        var filled = EmbeddingModelDefaults.Apply(Embedding(remoteModelId), "meisterdev/liteLlm", []);

        Assert.Equal(("cl100k_base", (int?)8191, (int?)dimensions), (filled.TokenizerName, filled.MaxInputTokens, filled.EmbeddingDimensions));
    }

    // The table is consulted before the catalog, so a catalog entry stating another context does not change the
    // documented input limit.
    [Fact]
    public void TheTableComesBeforeTheCatalog()
    {
        var filled = EmbeddingModelDefaults.Apply(
            Embedding("text-embedding-3-small"),
            "meisterdev/openAi",
            [Catalog("openai", "text-embedding-3-small", context: 4096)]);

        Assert.Equal(8191, filled.MaxInputTokens);
    }

    [Fact]
    public void AModelOnlyTheCatalogKnowsTakesItsInputLimitFromTheCatalog()
    {
        var filled = EmbeddingModelDefaults.Apply(
            Embedding("mistral-embed"),
            "meisterdev/openAiCompatible",
            [Catalog("mistral", "mistral-embed", context: 8000), Catalog("openai", "gpt-4o", context: 128_000)]);

        Assert.Equal(
            (EmbeddingModelDefaults.FallbackTokenizerName, (int?)8000, (int?)EmbeddingModelDefaults.VectorDimensions),
            (filled.TokenizerName, filled.MaxInputTokens, filled.EmbeddingDimensions));
    }

    [Fact]
    public void ACatalogMatchIsFoundWithoutTheVendorPrefix()
    {
        var filled = EmbeddingModelDefaults.Apply(
            Embedding("mistral/mistral-embed"),
            "meisterdev/liteLlm",
            [Catalog("mistral", "mistral-embed", context: 8000)]);

        Assert.Equal(8000, filled.MaxInputTokens);
    }

    // Two catalog providers stating different contexts leave the limit undecided, so the fallback applies; the
    // family's own catalog provider decides it for that family.
    [Theory]
    [InlineData("meisterdev/openAiCompatible", 8191)]
    [InlineData("meisterdev/googleVertex", 2048)]
    public void CatalogEntriesThatDisagreeDecideNothingUnlessOneIsTheFamilysOwn(string providerKind, int expected)
    {
        var filled = EmbeddingModelDefaults.Apply(
            Embedding("gemini-embedding-001"),
            providerKind,
            [Catalog("google-vertex", "gemini-embedding-001", context: 2048), Catalog("google", "gemini-embedding-001", context: 4096)]);

        Assert.Equal(expected, filled.MaxInputTokens);
    }

    [Fact]
    public void AModelNobodyKnowsGetsTheFallbackValues()
    {
        var filled = EmbeddingModelDefaults.Apply(Embedding("bge-m3"), "meisterdev/openAiCompatible", []);

        Assert.Equal(("cl100k_base", (int?)8191, (int?)1536), (filled.TokenizerName, filled.MaxInputTokens, filled.EmbeddingDimensions));
    }

    [Fact]
    public void StatedValuesAreKept()
    {
        var stated = Embedding("text-embedding-3-large") with
        {
            TokenizerName = "o200k_base",
            MaxInputTokens = 2048,
            EmbeddingDimensions = 1536,
        };

        Assert.Equal(stated, EmbeddingModelDefaults.Apply(stated, "meisterdev/openAi", [Catalog("openai", "text-embedding-3-large", context: 4)]));
    }

    [Fact]
    public void OnlyTheMissingValueIsFilled()
    {
        var filled = EmbeddingModelDefaults.Apply(
            Embedding("text-embedding-3-large") with { EmbeddingDimensions = 1536 },
            "meisterdev/openAi",
            []);

        Assert.Equal(("cl100k_base", (int?)8191, (int?)1536), (filled.TokenizerName, filled.MaxInputTokens, filled.EmbeddingDimensions));
    }

    [Fact]
    public void AChatModelIsLeftAlone()
    {
        var chat = new AiConfiguredModelDto(Guid.Empty, "gpt-4o", "gpt-4o", [AiOperationKind.Chat], [ProviderDeclaredProtocolModes.Auto]);

        Assert.Same(chat, EmbeddingModelDefaults.Apply(chat, "meisterdev/openAi", []));
    }

    [Theory]
    [InlineData("cl100k_base", true)]
    [InlineData("O200K_BASE", true)]
    [InlineData(" p50k_edit ", true)]
    [InlineData("claude", false)]
    [InlineData("gpt2", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyTheEncodingsTheEmbeddingPathsCountWithAreSupported(string? tokenizerName, bool supported)
    {
        Assert.Equal(supported, EmbeddingModelDefaults.IsSupportedTokenizer(tokenizerName));
    }

    [Fact]
    public void ARefusedTokenizerNamesTheSupportedOnes()
    {
        var refusal = EmbeddingModelDefaults.TokenizerRefusal("claude", "bge-m3");

        Assert.NotNull(refusal);
        Assert.All(EmbeddingModelDefaults.SupportedTokenizerNames, name => Assert.Contains(name, refusal, StringComparison.Ordinal));
        Assert.DoesNotContain("claude", refusal, StringComparison.Ordinal);
        Assert.Null(EmbeddingModelDefaults.TokenizerRefusal(null, "bge-m3"));
        Assert.Null(EmbeddingModelDefaults.TokenizerRefusal("cl100k_base", "bge-m3"));
    }

    private static AiConfiguredModelDto Embedding(string remoteModelId)
    {
        return new AiConfiguredModelDto(
            Guid.Empty,
            remoteModelId,
            remoteModelId,
            [AiOperationKind.Embedding],
            [ProviderDeclaredProtocolModes.Auto, ProviderDeclaredProtocolModes.Embeddings]);
    }

    private static AiModelCatalogEntryDto Catalog(string providerId, string remoteModelId, int? context)
    {
        return new AiModelCatalogEntryDto(
            providerId, providerId, remoteModelId, remoteModelId, null, false, false, false, false, null, context, null,
            null, null, null, null, false, null, AiModelCatalogLayer.Global);
    }
}
