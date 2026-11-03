// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Transport;

namespace MeisterDev.Ai.Providers.Tests.Transport;

/// <summary>
///     What discovery makes of a model-listing body, including the bodies a provider should not have sent.
/// </summary>
public sealed class ProviderModelListJsonTests
{
    [Fact]
    public void IdentifiersAreReadSortedAndDeduplicated()
    {
        Assert.True(ProviderModelListJson.TryReadModelIds("""{"data":[{"id":"gpt-b"},{"id":"gpt-a"},{"id":"GPT-A"}]}""", out var models));

        Assert.Equal(["gpt-a", "gpt-b"], models);
    }

    [Fact]
    public void TheModeAndTheInputLimitAreReadWhereTheListingStatesThem()
    {
        Assert.True(
            ProviderModelListJson.TryReadModels(
                """
                {"data":[
                  {"id":"bge-m3","mode":" Embedding ","max_input_tokens":8191},
                  {"id":"gpt-6.1-terra","mode":"chat","max_input_tokens":"many"},
                  {"id":"auto","mode":7,"max_input_tokens":-1}
                ]}
                """, out var models));

        Assert.Equal(
            [
                new ProviderListedModel("auto"),
                new ProviderListedModel("bge-m3", "Embedding", 8191),
                new ProviderListedModel("gpt-6.1-terra", "chat"),
            ],
            models);
    }

    [Theory]
    [InlineData("text-embedding-3-small", null, AiOperationKind.Embedding)]
    [InlineData("gpt-6.1-terra", null, AiOperationKind.Chat)]
    [InlineData("bge-m3", "embedding", AiOperationKind.Embedding)]
    [InlineData("embedding-router", "chat", AiOperationKind.Chat)]
    [InlineData("gpt-instruct", "completion", AiOperationKind.Chat)]
    [InlineData("o-pro", "Responses", AiOperationKind.Chat)]
    [InlineData("dall-e-3", "image_generation", null)]
    [InlineData("whisper", "audio_transcription", null)]
    [InlineData("rerank-v3", "rerank", null)]
    public void TheStatedModeDecidesWhatAListedModelServes(string id, string? mode, AiOperationKind? expected)
    {
        Assert.Equal(expected, ProviderModelListJson.OperationOf(new ProviderListedModel(id, mode)));
    }

    [Fact]
    public void AnEmptyBodyIsAnEmptyListing()
    {
        Assert.True(ProviderModelListJson.TryReadModelIds("", out var models));
        Assert.Empty(models);
    }

    [Fact]
    public void MalformedJsonIsRefusedRatherThanRaised()
    {
        Assert.False(ProviderModelListJson.TryReadModelIds("<html>gateway error</html>", out var models));
        Assert.Null(models);
    }

    [Fact]
    public void ANumericIdentifierIsSkippedAndTheRestIsStillRead()
    {
        Assert.True(ProviderModelListJson.TryReadModelIds("""{"data":[{"id":7},{"id":"gpt-a"}]}""", out var models));

        Assert.Equal(["gpt-a"], models);
    }

    [Fact]
    public void ADataPropertyThatIsNotAnArrayIsAnEmptyListing()
    {
        Assert.True(ProviderModelListJson.TryReadModelIds("""{"data":"nope"}""", out var models));
        Assert.Empty(models);
    }

    [Fact]
    public void AnEntryThatIsNotAnObjectIsSkipped()
    {
        Assert.True(ProviderModelListJson.TryReadModelIds("""{"data":["gpt-a",{"id":"gpt-b"}]}""", out var models));
        Assert.Equal(["gpt-b"], models);
    }
}
