// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Usage;
using MeisterDev.ProPR.Application.AI;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Application.Tests.AI;

/// <summary>Unit tests for <see cref="AiTokenUsageExtractor" />.</summary>
public sealed class AiTokenUsageExtractorTests
{
    [Fact]
    public void FromResponse_ReadsNativeCacheAndReasoningCounts()
    {
        var response = new ChatResponse
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 1000,
                OutputTokenCount = 400,
                CachedInputTokenCount = 250,
                ReasoningTokenCount = 120,
            },
        };

        var usage = AiTokenUsageExtractor.FromResponse(response);

        Assert.False(usage.IsEstimated);
        Assert.Equal(1000L, usage.InputTokens);
        Assert.Equal(400L, usage.OutputTokens);
        Assert.Equal(250L, usage.CachedInputTokens);
        Assert.Equal(120L, usage.ReasoningTokens);
        Assert.Equal(0L, usage.CacheWriteTokens);

        // Invariant: Input == nonCached + cached + cacheWrite.
        Assert.Equal(750L, usage.NonCachedInputTokens);
        Assert.Equal(usage.InputTokens, usage.NonCachedInputTokens + usage.CachedInputTokens + usage.CacheWriteTokens);
    }

    [Fact]
    public void FromResponse_NullUsage_IsFlaggedEstimatedWithZeroCounts()
    {
        var response = new ChatResponse { Usage = null };

        var usage = AiTokenUsageExtractor.FromResponse(response);

        Assert.True(usage.IsEstimated);
        Assert.Equal(0L, usage.InputTokens);
        Assert.Equal(0L, usage.OutputTokens);
        Assert.Equal(0L, usage.CachedInputTokens);
        Assert.Equal(0L, usage.ReasoningTokens);
        Assert.Equal(0L, usage.CacheWriteTokens);
    }

    [Fact]
    public void FromResponse_NullResponse_IsFlaggedEstimated()
    {
        var usage = AiTokenUsageExtractor.FromResponse(null);

        Assert.True(usage.IsEstimated);
        Assert.Equal(0L, usage.InputTokens);
    }

    [Fact]
    public void FromResponse_MissingNativeCounts_CoalesceToZeroWithoutEstimatedFlag()
    {
        var response = new ChatResponse
        {
            Usage = new UsageDetails { InputTokenCount = 500, OutputTokenCount = 100 },
        };

        var usage = AiTokenUsageExtractor.FromResponse(response);

        Assert.False(usage.IsEstimated);
        Assert.Equal(0L, usage.CachedInputTokens);
        Assert.Equal(0L, usage.ReasoningTokens);
    }

    // Cache-write reaches the domain under the name the host owns, whichever family produced the call. The
    // family's own spelling stopped at its driver, so nothing here has to know which one answered.
    [Fact]
    public void FromResponse_ReadsCacheWriteUnderTheHostOwnedName()
    {
        var response = new ChatResponse
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 500,
                OutputTokenCount = 100,
                AdditionalCounts = new AdditionalPropertiesDictionary<long>
                {
                    [ProviderTokenUsage.CacheWriteCountName] = 80,
                },
            },
        };

        var usage = AiTokenUsageExtractor.FromResponse(response);

        Assert.Equal(80L, usage.CacheWriteTokens);
    }

    // A vendor's own spelling is not read here. Reading one would put a family's vocabulary back into the host
    // and meter a second family that happened to use the same word by a rule nobody wrote for it.
    [Fact]
    public void FromResponse_DoesNotReadAVendorsOwnCounterName()
    {
        var response = new ChatResponse
        {
            Usage = new UsageDetails
            {
                InputTokenCount = 500,
                OutputTokenCount = 100,
                AdditionalCounts = new AdditionalPropertiesDictionary<long>
                {
                    ["cache_creation_input_tokens"] = 80,
                    ["OutputTokenDetails.AudioTokenCount"] = 10,
                },
            },
        };

        var usage = AiTokenUsageExtractor.FromResponse(response);

        Assert.Equal(0L, usage.CacheWriteTokens);
    }
}
