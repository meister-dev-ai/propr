// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Resilience;

namespace MeisterDev.Ai.Providers.Tests.Resilience;

/// <summary>
///     The pair a verdict cannot hold at once, and the three factories that never produce it.
/// </summary>
/// <remarks>
///     A verdict marked throttled and not transient holds back every other call bound for the connection and
///     repeats none of them, so the connection paces itself for a call nothing retries.
/// </remarks>
public sealed class ProviderFailureVerdictTests
{
    [Fact]
    public void AThrottledAndPermanentVerdictIsRefused()
    {
        Assert.Throws<ArgumentException>(() =>
            new ProviderFailureVerdict(IsTransient: false, "quota", IsThrottled: true));
    }

    [Fact]
    public void MakingAThrottledVerdictPermanentIsRefused()
    {
        var throttled = ProviderFailureVerdict.Throttled("quota");

        Assert.Throws<ArgumentException>(() => throttled with { IsTransient = false });
    }

    [Fact]
    public void MarkingAPermanentVerdictThrottledIsRefused()
    {
        var permanent = ProviderFailureVerdict.Permanent("bad key", 401);

        Assert.Throws<ArgumentException>(() => permanent with { IsThrottled = true });
    }

    [Fact]
    public void TheThreeFactoriesProduceTheExpectedPair()
    {
        Assert.Equal((false, false), Pair(ProviderFailureVerdict.Permanent("bad key", 401)));
        Assert.Equal((true, false), Pair(ProviderFailureVerdict.Transient("gateway", httpStatus: 502)));
        Assert.Equal((true, true), Pair(ProviderFailureVerdict.Throttled("quota", httpStatus: 429)));
    }

    // Both together in one expression, which the initializers see in source order.
    [Fact]
    public void TurningAPermanentVerdictIntoAThrottledOneIsAccepted()
    {
        var retimed = ProviderFailureVerdict.Permanent("bad key") with { IsTransient = true, IsThrottled = true };

        Assert.Equal((true, true), Pair(retimed));
    }

    private static (bool IsTransient, bool IsThrottled) Pair(ProviderFailureVerdict verdict)
    {
        return (verdict.IsTransient, verdict.IsThrottled);
    }
}
