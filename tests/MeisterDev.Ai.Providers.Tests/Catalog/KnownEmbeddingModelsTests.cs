// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Catalog;

namespace MeisterDev.Ai.Providers.Tests.Catalog;

public sealed class KnownEmbeddingModelsTests
{
    [Theory]
    [InlineData("text-embedding-3-small", 1536)]
    [InlineData("text-embedding-3-large", 3072)]
    [InlineData("text-embedding-ada-002", 1536)]
    public void AnOpenAiEmbeddingModelIsFoundWithItsDocumentedValues(string remoteModelId, int dimensions)
    {
        Assert.True(KnownEmbeddingModels.TryFind(remoteModelId, out var model));
        Assert.Equal(("cl100k_base", 8191, dimensions), (model.TokenizerName, model.MaxInputTokens, model.Dimensions));
    }

    // A gateway publishes a vendor model under the vendor's name, and an operator types the identifier in any case.
    [Theory]
    [InlineData("openai/text-embedding-3-large")]
    [InlineData("OpenAI/Text-Embedding-3-Large")]
    [InlineData("openrouter/openai/text-embedding-3-large")]
    [InlineData("  text-embedding-3-large  ")]
    public void APrefixedOrDifferentlyCasedIdentifierFindsTheSameModel(string remoteModelId)
    {
        Assert.True(KnownEmbeddingModels.TryFind(remoteModelId, out var model));
        Assert.Equal("text-embedding-3-large", model.RemoteModelId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bge-m3")]
    [InlineData("openai/")]
    [InlineData("text-embedding-3-large-preview")]
    public void AModelThatIsNotInTheTableIsNotFound(string? remoteModelId)
    {
        Assert.False(KnownEmbeddingModels.TryFind(remoteModelId, out var model));
        Assert.Null(model);
    }

    [Theory]
    [InlineData("openai/text-embedding-3-small", "text-embedding-3-small")]
    [InlineData("a/b/c", "c")]
    [InlineData("bge-m3", "bge-m3")]
    [InlineData("vendor/", "vendor/")]
    public void TheVendorPrefixIsTheIdentifierUpToTheLastSlash(string remoteModelId, string expected)
    {
        Assert.Equal(expected, KnownEmbeddingModels.WithoutVendorPrefix(remoteModelId));
    }
}
