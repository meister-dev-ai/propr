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

    // An empty per-provider key list does NOT suppress the shared names: a provider whose entry is empty still
    // falls back to them. The map can therefore point a provider at different names but never disable reading.
    // Pinned because the difference only shows up once a provider must be stopped from reading a name.
    [Theory]
    [InlineData(AiProviderKind.AzureOpenAi)]
    [InlineData(AiProviderKind.OpenAi)]
    [InlineData(AiProviderKind.LiteLlm)]
    [InlineData(AiProviderKind.OpenAiCompatible)]
    public void ProviderWithAnEmptyKeyList_StillFallsBackToTheSharedNames(AiProviderKind providerKind)
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

    // Most callers extract usage without knowing which provider answered. Recovering the counter anyway is the
    // difference between an understated bill and a correct one, so the counter is found with no kind supplied.
    [Fact]
    public void CountersAreRecoveredEvenWhenTheProviderIsNotKnownToTheCaller()
    {
        var usage = ProviderUsageExtractor.FromUsage(
            new UsageDetails
            {
                InputTokenCount = 200,
                OutputTokenCount = 50,
                AdditionalCounts = new AdditionalPropertiesDictionary<long>
                {
                    ["cache_read_input_tokens"] = 120,
                    ["cache_creation_input_tokens"] = 80,
                    ["reasoning_tokens"] = 30,
                },
            });

        Assert.Equal(120, usage.CachedInputTokens);
        Assert.Equal(80, usage.CacheWriteTokens);
        Assert.Equal(30, usage.ReasoningTokens);
    }

    // A gateway that reports reasoning only under its own name would otherwise show a reasoning model spending
    // nothing on reasoning — the counter is there, just not where the client library looks.
    [Fact]
    public void ReasoningIsRecoveredWhenTheAdapterDidNotMapIt()
    {
        var usage = ProviderUsageExtractor.FromUsage(
            new UsageDetails
            {
                InputTokenCount = 10,
                OutputTokenCount = 90,
                AdditionalCounts = new AdditionalPropertiesDictionary<long> { ["completion_tokens_details.reasoning_tokens"] = 64 },
            },
            AiProviderKind.OpenAiCompatible);

        Assert.Equal(64, usage.ReasoningTokens);
    }

    // A mapped counter beats a name lookup, and a mapped zero is a measured zero rather than a gap to fill in.
    [Fact]
    public void AMappedCounterWinsOverAnAdditionalCountOfTheSameThing()
    {
        var usage = ProviderUsageExtractor.FromUsage(
            new UsageDetails
            {
                InputTokenCount = 100,
                OutputTokenCount = 20,
                CachedInputTokenCount = 0,
                ReasoningTokenCount = 7,
                AdditionalCounts = new AdditionalPropertiesDictionary<long>
                {
                    ["cached_tokens"] = 99,
                    ["reasoning_tokens"] = 99,
                },
            });

        Assert.Equal(0, usage.CachedInputTokens);
        Assert.Equal(7, usage.ReasoningTokens);
    }
}
