// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Tests.Contracts;

/// <summary>
///     What a driver may report about a model it discovered.
/// </summary>
/// <remarks>
///     Every value here comes from a provider's own listing by way of a driver, and each one reaches a form an
///     operator picks from and a row a review is billed against. A value that is not a fact about a model is
///     refused where the driver states it, so it is the driver that is wrong and not the connection.
/// </remarks>
public sealed class ProviderDiscoveredModelTests
{
    // Several providers list a model with no name. A blank one renders as an empty row an operator cannot pick,
    // and the fallback was documented on the type and applied by nobody.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AModelWithNoNameIsNamedByItsIdentifier(string displayName)
    {
        Assert.Equal("gpt-5", Chat(displayName).DisplayName);
        Assert.Equal("gpt-5", (Chat("Named") with { DisplayName = displayName }).DisplayName);
    }

    [Fact]
    public void ANameTheProviderStatedIsKept()
    {
        Assert.Equal("GPT 5", Chat("GPT 5").DisplayName);
    }

    // The bounds are nullable, so a provider that states nothing is already expressible. A zero says the model
    // takes no tokens, which reaches the context budget as a model nothing fits in.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ATokenBoundThatIsNotPositiveIsRefused(int bound)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Chat("GPT 5") with { MaxInputTokens = bound });
        Assert.Throws<ArgumentOutOfRangeException>(() => Chat("GPT 5") with { MaxContextTokens = bound });
    }

    [Fact]
    public void AnEmbeddingWidthThatIsNotPositiveIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderDiscoveredModel(
            "text-embedding-3-small",
            "text-embedding-3-small",
            [AiOperationKind.Embedding],
            [ProviderDeclaredProtocolModes.Embeddings],
            EmbeddingDimensions: 0));
    }

    // A width on a model that embeds nothing is read by nothing, and it says the driver has the model wrong.
    [Fact]
    public void AnEmbeddingWidthOnAModelThatEmbedsNothingIsRefused()
    {
        Assert.Throws<ArgumentException>(() => Chat("GPT 5") with { EmbeddingDimensions = 1536 });
    }

    [Fact]
    public void AnEmbeddingModelCarriesItsWidth()
    {
        var model = new ProviderDiscoveredModel(
            "text-embedding-3-large",
            "text-embedding-3-large",
            [AiOperationKind.Embedding],
            [ProviderDeclaredProtocolModes.Embeddings],
            EmbeddingDimensions: 3072);

        Assert.Equal(3072, model.EmbeddingDimensions);
    }

    private static ProviderDiscoveredModel Chat(string displayName)
    {
        return new ProviderDiscoveredModel(
            "gpt-5",
            displayName,
            [AiOperationKind.Chat],
            [ProviderDeclaredProtocolModes.Auto]);
    }
}
