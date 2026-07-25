// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;
using MeisterDev.Ai.Providers.Usage;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Tests.Usage;

public sealed class ProviderUsageExtractorTests
{
    [Fact]
    public void NoUsagePayload_YieldsEstimatedZero_NotMeasuredZero()
    {
        // The distinction matters: a measured zero would be indistinguishable from a provider that
        // reported nothing, and cost accounting would silently treat the call as free.
        var usage = ProviderUsageExtractor.FromUsage(null);

        Assert.True(usage.IsEstimated);
        Assert.Equal(0, usage.InputTokens);
        Assert.Equal(0, usage.OutputTokens);
    }

    [Fact]
    public void NativeCounts_AreReadFromUsageDetails()
    {
        var usage = ProviderUsageExtractor.FromUsage(
            new UsageDetails
            {
                InputTokenCount = 100,
                OutputTokenCount = 40,
                CachedInputTokenCount = 25,
                ReasoningTokenCount = 12,
            });

        Assert.False(usage.IsEstimated);
        Assert.Equal(100, usage.InputTokens);
        Assert.Equal(40, usage.OutputTokens);
        Assert.Equal(25, usage.CachedInputTokens);
        Assert.Equal(12, usage.ReasoningTokens);
        Assert.Equal(0, usage.CacheWriteTokens);
    }

    // Cache-write is the extension seam for providers that bill cache creation. No OpenAI-family provider
    // reports it, so the default key set is what a newly added provider hits before it gets its own entry.
    [Fact]
    public void CacheWriteTokens_AreReadFromAdditionalCounts_ViaTheDefaultKeys()
    {
        var usage = ProviderUsageExtractor.FromUsage(
            new UsageDetails
            {
                InputTokenCount = 10,
                OutputTokenCount = 5,
                AdditionalCounts = new AdditionalPropertiesDictionary<long> { ["cache_creation_input_tokens"] = 512 },
            });

        Assert.Equal(512, usage.CacheWriteTokens);
    }

    // An empty per-provider key list does NOT suppress the default keys: the map is consulted only when it
    // yields a non-empty list, so a provider whose entry is empty still falls back to the defaults. The map
    // can therefore override the default keys but never disable them. Pinned because the difference only
    // shows up once a provider must be stopped from reading a key rather than pointed at a different one.
    [Theory]
    [InlineData(AiProviderKind.AzureOpenAi)]
    [InlineData(AiProviderKind.OpenAi)]
    [InlineData(AiProviderKind.LiteLlm)]
    public void ProviderWithAnEmptyKeyList_StillFallsBackToTheDefaultKeys(AiProviderKind providerKind)
    {
        var usage = ProviderUsageExtractor.FromUsage(
            new UsageDetails
            {
                InputTokenCount = 10,
                OutputTokenCount = 5,
                AdditionalCounts = new AdditionalPropertiesDictionary<long> { ["cache_creation_input_tokens"] = 512 },
            },
            providerKind);

        Assert.Equal(512, usage.CacheWriteTokens);
    }
}
