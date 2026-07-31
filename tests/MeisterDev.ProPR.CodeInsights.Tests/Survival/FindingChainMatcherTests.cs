using MeisterDev.ProPR.CodeInsights.Survival;

// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.


namespace MeisterDev.ProPR.CodeInsights.Tests.Survival;

/// <summary>
///     Linking a finding to the problem an earlier increment already raised. This is what makes "was it still
///     being raised at the end" answerable, and getting it wrong in either direction misreports durability.
/// </summary>
public sealed class FindingChainMatcherTests
{
    private static readonly Guid ChainA = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid ChainB = Guid.Parse("22222222-0000-0000-0000-000000000002");

    private const string Original =
        "The retry loop has no ceiling: a persistent 409 from the payment gateway will retry indefinitely.";

    [Fact]
    public void TheSameProblemRestatedContinuesItsChain()
    {
        // Wording drifts between increments even when the problem has not changed.
        var restated =
            "The retry loop still has no ceiling: a persistent 409 from the payment gateway retries indefinitely.";

        var chain = FindingChainMatcher.FindContinuedChain(
            "src/Payments/RefundProcessor.cs",
            restated,
            [new FindingChainCandidate(ChainA, "src/Payments/RefundProcessor.cs", Original)],
            new HashSet<Guid>());

        Assert.Equal(ChainA, chain);
    }

    [Fact]
    public void AnUnrelatedProblemInTheSameFileStartsItsOwnChain()
    {
        var chain = FindingChainMatcher.FindContinuedChain(
            "src/Payments/RefundProcessor.cs",
            "The currency code is compared case-sensitively, so 'eur' never matches 'EUR'.",
            [new FindingChainCandidate(ChainA, "src/Payments/RefundProcessor.cs", Original)],
            new HashSet<Guid>());

        Assert.Null(chain);
    }

    [Fact]
    public void TheSameWordingInADifferentFileIsADifferentProblem()
    {
        // Otherwise one repeated concern would collapse every file that shares its vocabulary into one chain.
        var chain = FindingChainMatcher.FindContinuedChain(
            "src/Billing/RetryPolicy.cs",
            Original,
            [new FindingChainCandidate(ChainA, "src/Payments/RefundProcessor.cs", Original)],
            new HashSet<Guid>());

        Assert.Null(chain);
    }

    [Fact]
    public void AFileAnchoredFindingNeverContinuesAPullRequestLevelOne()
    {
        // A summary remark would otherwise absorb whichever inline finding shared its vocabulary.
        var chain = FindingChainMatcher.FindContinuedChain(
            "src/Payments/RefundProcessor.cs",
            Original,
            [new FindingChainCandidate(ChainA, null, Original)],
            new HashSet<Guid>());

        Assert.Null(chain);
    }

    [Fact]
    public void APullRequestLevelFindingContinuesAPullRequestLevelChain()
    {
        var chain = FindingChainMatcher.FindContinuedChain(
            null,
            Original,
            [new FindingChainCandidate(ChainA, null, Original)],
            new HashSet<Guid>());

        Assert.Equal(ChainA, chain);
    }

    [Fact]
    public void AChainAlreadyContinuedThisIncrementIsNotClaimedTwice()
    {
        // Two findings in one increment are two problems even when they read alike; letting both claim the same
        // predecessor would silently merge them and undercount what was raised.
        var chain = FindingChainMatcher.FindContinuedChain(
            "src/Payments/RefundProcessor.cs",
            Original,
            [new FindingChainCandidate(ChainA, "src/Payments/RefundProcessor.cs", Original)],
            new HashSet<Guid> { ChainA });

        Assert.Null(chain);
    }

    [Fact]
    public void TheBestMatchWinsRatherThanTheFirstEnumerated()
    {
        // With several near-identical predecessors, taking whichever came first would attach the chain arbitrarily.
        var weaker = "The retry loop has no ceiling somewhere in the gateway path.";

        var chain = FindingChainMatcher.FindContinuedChain(
            "src/Payments/RefundProcessor.cs",
            Original,
            [
                new FindingChainCandidate(ChainB, "src/Payments/RefundProcessor.cs", weaker),
                new FindingChainCandidate(ChainA, "src/Payments/RefundProcessor.cs", Original),
            ],
            new HashSet<Guid>());

        Assert.Equal(ChainA, chain);
    }

    [Fact]
    public void NothingEarlierMeansANewChain()
    {
        Assert.Null(FindingChainMatcher.FindContinuedChain("a.cs", Original, [], new HashSet<Guid>()));
    }

    [Fact]
    public void AnEmptyMessageIsNeverAContinuation()
    {
        var chain = FindingChainMatcher.FindContinuedChain(
            "a.cs",
            "   ",
            [new FindingChainCandidate(ChainA, "a.cs", Original)],
            new HashSet<Guid>());

        Assert.Null(chain);
    }
}
