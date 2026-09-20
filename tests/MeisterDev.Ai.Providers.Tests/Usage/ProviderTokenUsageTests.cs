// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Usage;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Tests.Usage;

/// <summary>
///     Covers the relationship between the five counters, which is the part of the usage shape a driver has to
///     satisfy rather than merely populate.
/// </summary>
/// <remarks>
///     The host prices non-cached input at the full input rate and each cache bucket at its own, so a driver that
///     hands back a vendor's exclusive counts bills the whole prompt at nothing. Nothing about that failure is
///     visible in the counts themselves — every counter is present and every number is plausible — which is why
///     the relationship is expressed as something that can be checked.
/// </remarks>
public sealed class ProviderTokenUsageTests
{
    [Fact]
    public void NonCachedInputIsTheInputTotalLessBothCacheBuckets()
    {
        var usage = new ProviderTokenUsage(1000, 200, CachedInputTokens: 300, CacheWriteTokens: 100);

        Assert.Equal(600, usage.NonCachedInputTokens);
    }

    // The floor is what stops a negative charge reaching the cost calculation. It is also what makes an
    // un-normalized driver look free instead of looking broken, which is the reason the inclusion rule exists.
    [Fact]
    public void NonCachedInputIsFlooredAtZeroWhenTheCacheBucketsExceedTheInputTotal()
    {
        var usage = new ProviderTokenUsage(50, 200, CachedInputTokens: 900, CacheWriteTokens: 400);

        Assert.Equal(0, usage.NonCachedInputTokens);
    }

    [Fact]
    public void CountersThatCoverTheirPortionsSatisfyTheInclusionRule()
    {
        var usage = new ProviderTokenUsage(1000, 200, CachedInputTokens: 300, CacheWriteTokens: 100, ReasoningTokens: 150);

        Assert.True(usage.CountersAreInclusive);
    }

    // The shape a vendor that reports input exclusive of its cache buckets produces: a small input count beside a
    // large cache read, which prices the prompt at nothing once the floor applies.
    [Fact]
    public void InputThatExcludesTheCacheBucketsFailsTheInclusionRule()
    {
        var usage = new ProviderTokenUsage(120, 200, CachedInputTokens: 4000, CacheWriteTokens: 300);

        Assert.False(usage.CountersAreInclusive);
        Assert.Equal(0, usage.NonCachedInputTokens);
    }

    [Fact]
    public void OutputThatExcludesTheReasoningPortionFailsTheInclusionRule()
    {
        var usage = new ProviderTokenUsage(1000, 100, ReasoningTokens: 150);

        Assert.False(usage.CountersAreInclusive);
    }

    [Fact]
    public void TheAllZeroValuesSatisfyTheInclusionRule()
    {
        Assert.True(ProviderTokenUsage.Zero.CountersAreInclusive);
        Assert.True(ProviderTokenUsage.Missing.CountersAreInclusive);
        Assert.True(ProviderTokenUsage.Missing.IsEstimated);
        Assert.False(ProviderTokenUsage.Zero.IsEstimated);
    }

    // The distinction matters: a measured zero would be indistinguishable from a provider that reported nothing,
    // and cost accounting would silently treat the call as free.
    [Fact]
    public void AMissingUsagePayloadReadsAsEstimatedZeroRatherThanMeasuredZero()
    {
        var usage = ProviderTokenUsage.FromUsageDetails(null);

        Assert.True(usage.IsEstimated);
        Assert.Equal(0, usage.InputTokens);
        Assert.Equal(0, usage.OutputTokens);
    }

    [Fact]
    public void ThePropertiesAPayloadStatesAreReadAsTheyStandWithNoVendorNameLookup()
    {
        var usage = ProviderTokenUsage.FromUsageDetails(
            new UsageDetails
            {
                InputTokenCount = 100,
                OutputTokenCount = 40,
                CachedInputTokenCount = 25,
                ReasoningTokenCount = 12,
                AdditionalCounts = new AdditionalPropertiesDictionary<long>
                {
                    // A vendor spelling, which nothing here reads: the family that uses it maps it itself.
                    ["cache_creation_input_tokens"] = 512,
                },
            });

        Assert.False(usage.IsEstimated);
        Assert.Equal(100, usage.InputTokens);
        Assert.Equal(40, usage.OutputTokens);
        Assert.Equal(25, usage.CachedInputTokens);
        Assert.Equal(12, usage.ReasoningTokens);
        Assert.Equal(0, usage.CacheWriteTokens);
    }

    // Cache-write is the one counter with no property of its own, so it travels under a name the host owns. A
    // family maps its vendor's spelling onto that name; nothing downstream reads the vendor's.
    [Fact]
    public void CacheWriteTravelsUnderTheHostOwnedName()
    {
        var usage = new ProviderTokenUsage(4170, 207, CachedInputTokens: 4000, CacheWriteTokens: 50, ReasoningTokens: 200);

        var round = ProviderTokenUsage.FromUsageDetails(usage.ToUsageDetails());

        Assert.Equal(usage, round);
    }

    // A response the provider reported no usage for becomes an all-zero payload on the way out, and a payload
    // that is present reads back as measured. Unmarked, a call whose provider said nothing is recorded as a call
    // that cost nothing, and those two are the difference between a quota that ran out and a free prompt.
    [Fact]
    public void AnEstimatedUsageSurvivesTheRoundTripThroughAPayload()
    {
        var round = ProviderTokenUsage.FromUsageDetails(ProviderTokenUsage.Missing.ToUsageDetails());

        Assert.True(round.IsEstimated);
        Assert.Equal(ProviderTokenUsage.Missing, round);
    }

    [Fact]
    public void AMeasuredUsageDoesNotComeBackEstimated()
    {
        var round = ProviderTokenUsage.FromUsageDetails(ProviderTokenUsage.Zero.ToUsageDetails());

        Assert.False(round.IsEstimated);
    }

    // The host prices against these five. A negative one reaches the spend record as a credit against work that
    // happened, and it passes the inclusion check too, so the check meant to catch a mis-normalized driver would
    // report it as sound.
    [Theory]
    [InlineData(-1, 0, 0, 0, 0)]
    [InlineData(0, -1, 0, 0, 0)]
    [InlineData(0, 0, -1, 0, 0)]
    [InlineData(0, 0, 0, -1, 0)]
    [InlineData(0, 0, 0, 0, -1)]
    public void ANegativeCounterIsRefused(long input, long output, long cached, long cacheWrite, long reasoning)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProviderTokenUsage(input, output, cached, cacheWrite, reasoning));
    }

    // A `with` expression writes over what the constructor set, so the check has to be on the accessor and not
    // only on the positional argument.
    [Fact]
    public void ANegativeCounterIsRefusedOnACopyAsWell()
    {
        var usage = new ProviderTokenUsage(100, 20);

        Assert.Throws<ArgumentOutOfRangeException>(() => usage with { OutputTokens = -1 });
    }

    // The total is the two headline counts added together. Adding the buckets as well would count cached input
    // twice, and the total is what an operator reads off a trace.
    [Fact]
    public void TheRestatedTotalCountsCachedInputOnce()
    {
        var details = new ProviderTokenUsage(4170, 207, CachedInputTokens: 4000, CacheWriteTokens: 50).ToUsageDetails();

        Assert.Equal(4377, details.TotalTokenCount);
    }
}
