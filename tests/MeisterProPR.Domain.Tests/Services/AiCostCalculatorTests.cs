// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Services;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Domain.Tests.Services;

public class AiCostCalculatorTests
{
    [Fact]
    public void Calculate_BothRatesNull_ReturnsNullApproximate()
    {
        var usage = new AiTokenUsage(1_000, 500, CachedInputTokens: 100);
        var pricing = new ModelPricing(null, null);

        var estimate = AiCostCalculator.Calculate(usage, pricing);

        Assert.Null(estimate.Usd);
        Assert.True(estimate.IsApproximate);
    }

    [Fact]
    public void Calculate_FullyPricedTier_ReturnsExactCostNotApproximate()
    {
        // NonCachedInput = 1_000_000 - 200_000 = 800_000.
        var usage = new AiTokenUsage(1_000_000, 500_000, CachedInputTokens: 200_000);
        var pricing = new ModelPricing(3m, 15m, 0.75m);

        var estimate = AiCostCalculator.Calculate(usage, pricing);

        // 800_000*3/1e6 + 200_000*0.75/1e6 + 500_000*15/1e6 = 2.4 + 0.15 + 7.5
        Assert.Equal(10.05m, estimate.Usd);
        Assert.False(estimate.IsApproximate);
    }

    [Fact]
    public void Calculate_CachedTokensNoCachedRate_FallsBackToInputRateAndFlagsApproximate()
    {
        var usage = new AiTokenUsage(1_000_000, 0, CachedInputTokens: 200_000);
        var pricing = new ModelPricing(3m, 15m);

        var estimate = AiCostCalculator.Calculate(usage, pricing);

        // Cached priced at the input rate: 800_000*3/1e6 + 200_000*3/1e6 = 2.4 + 0.6
        Assert.Equal(3.0m, estimate.Usd);
        Assert.True(estimate.IsApproximate);
    }

    [Fact]
    public void Calculate_CacheWriteTokens_PricedAtInputRateAndFlagsApproximate()
    {
        // NonCachedInput = 1_000_000 - 100_000 = 900_000.
        var usage = new AiTokenUsage(1_000_000, 0, CacheWriteTokens: 100_000);
        var pricing = new ModelPricing(3m, 15m, 0.75m);

        var estimate = AiCostCalculator.Calculate(usage, pricing);

        // 900_000*3/1e6 + 100_000*3/1e6 = 2.7 + 0.3
        Assert.Equal(3.0m, estimate.Usd);
        Assert.True(estimate.IsApproximate);
    }

    [Fact]
    public void Calculate_ReasoningBilledAtOutputRateWithNoSeparateTerm()
    {
        // Output already includes the 400 reasoning tokens; no extra reasoning term.
        var usage = new AiTokenUsage(0, 1_000, ReasoningTokens: 400);
        var pricing = new ModelPricing(3m, 15m);

        var estimate = AiCostCalculator.Calculate(usage, pricing);

        Assert.Equal(1_000 * 15m / 1_000_000m, estimate.Usd);
        Assert.False(estimate.IsApproximate);
    }

    [Fact]
    public void Calculate_EmbeddingEquivalentInputs_MatchesLegacyFormula()
    {
        var usage = new AiTokenUsage(1234, 567);
        var pricing = new ModelPricing(2m, 8m);

        var estimate = AiCostCalculator.Calculate(usage, pricing);

        var legacy = 2m * 1234 / 1_000_000m + 8m * 567 / 1_000_000m;
        Assert.Equal(legacy, estimate.Usd);
        Assert.False(estimate.IsApproximate);
    }

    [Fact]
    public void Calculate_OutputRateNullWithOutputTokens_FlagsApproximate()
    {
        var usage = new AiTokenUsage(1_000, 500);
        var pricing = new ModelPricing(3m, null);

        var estimate = AiCostCalculator.Calculate(usage, pricing);

        Assert.Equal(1_000 * 3m / 1_000_000m, estimate.Usd);
        Assert.True(estimate.IsApproximate);
    }

    [Fact]
    public void Calculate_InputRateNullWithInputTokens_FlagsApproximate()
    {
        var usage = new AiTokenUsage(1_000, 500);
        var pricing = new ModelPricing(null, 15m);

        var estimate = AiCostCalculator.Calculate(usage, pricing);

        Assert.Equal(500 * 15m / 1_000_000m, estimate.Usd);
        Assert.True(estimate.IsApproximate);
    }

    [Fact]
    public void Calculate_MissingUsage_FlagsApproximate()
    {
        var usage = new AiTokenUsage(1_000, 500, IsEstimated: true);
        var pricing = new ModelPricing(3m, 15m);

        var estimate = AiCostCalculator.Calculate(usage, pricing);

        Assert.NotNull(estimate.Usd);
        Assert.True(estimate.IsApproximate);
    }

    [Fact]
    public void Calculate_PreservesDecimalPrecisionWithoutRounding()
    {
        var usage = new AiTokenUsage(1, 0);
        var pricing = new ModelPricing(1m, 1m);

        var estimate = AiCostCalculator.Calculate(usage, pricing);

        Assert.Equal(0.000001m, estimate.Usd);
    }
}
