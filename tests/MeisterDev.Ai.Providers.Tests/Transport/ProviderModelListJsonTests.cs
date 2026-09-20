// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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
