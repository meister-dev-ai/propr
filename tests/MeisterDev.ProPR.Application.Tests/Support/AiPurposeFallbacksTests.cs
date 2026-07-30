// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Support;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Tests.Support;

/// <summary>
///     Which purpose a purpose degrades to when it has no model of its own. One definition, consulted from both
///     the connection-binding lookup and the logical-model role lookup.
/// </summary>
public sealed class AiPurposeFallbacksTests
{
    [Theory]
    [InlineData(AiPurpose.ReviewTriage, AiPurpose.ReviewLowEffort)]
    [InlineData(AiPurpose.ReviewVerification, AiPurpose.ReviewTriage)]
    [InlineData(AiPurpose.ReviewLowEffort, AiPurpose.ReviewDefault)]
    [InlineData(AiPurpose.ReviewMediumEffort, AiPurpose.ReviewDefault)]
    [InlineData(AiPurpose.ReviewHighEffort, AiPurpose.ReviewDefault)]
    [InlineData(AiPurpose.ProRVPrefilter, AiPurpose.ReviewDefault)]
    public void EachPurposeDegradesToItsCheaperRelative(AiPurpose purpose, AiPurpose expected)
    {
        Assert.Equal(expected, AiPurposeFallbacks.Next(purpose));
    }

    [Fact]
    public void InsightsClassificationDegradesToTheCheapTriageModel()
    {
        // Without this, an installation that switched Code Insights on would collect findings and classify none
        // of them: the purpose is new, so nothing has it bound, and an unbound purpose resolves to nothing.
        Assert.Equal(AiPurpose.ReviewTriage, AiPurposeFallbacks.Next(AiPurpose.InsightsClassification));
    }

    [Fact]
    public void InsightsClassificationReachesTheReviewDefaultThroughItsChain()
    {
        // The chain is what makes "bind one review model and everything works" true.
        Assert.Equal(
            [AiPurpose.ReviewTriage, AiPurpose.ReviewLowEffort, AiPurpose.ReviewDefault],
            AiPurposeFallbacks.Chain(AiPurpose.InsightsClassification));
    }

    [Fact]
    public void ThePurposesWithNothingCheaperToFallBackOnHaveNoFallback()
    {
        // The default is the end of every chat chain, and embeddings are a different capability entirely: an
        // embedding purpose falling back to a chat model would resolve to a model that cannot serve it.
        Assert.Null(AiPurposeFallbacks.Next(AiPurpose.ReviewDefault));
        Assert.Null(AiPurposeFallbacks.Next(AiPurpose.EmbeddingDefault));
        Assert.Empty(AiPurposeFallbacks.Chain(AiPurpose.ReviewDefault));
    }

    [Fact]
    public void EveryChainTerminates()
    {
        // Chain() is bounded against a cycle a future edit could introduce; this is the assertion that says so.
        foreach (var purpose in Enum.GetValues<AiPurpose>())
        {
            var chain = AiPurposeFallbacks.Chain(purpose).ToList();

            Assert.DoesNotContain(purpose, chain);
            Assert.Equal(chain.Distinct().Count(), chain.Count);
        }
    }
}
