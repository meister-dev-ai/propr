// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution.Services;

/// <summary>
///     The rule that keeps an unlicensed host to one review at a time. It applies to both fan-out axes,
///     because they multiply: capping jobs alone still lets one review fan out across files.
/// </summary>
public sealed class ReviewConcurrencyPolicyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(64)]
    public void WithTheCapability_TheConfiguredWidthIsWhatIsUsed(int configured)
    {
        Assert.Equal(configured, ReviewConcurrencyPolicy.Effective(configured, parallelReviewExecutionEnabled: true));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(64)]
    public void WithoutTheCapability_EveryConfiguredWidthCollapsesToOne(int configured)
    {
        Assert.Equal(1, ReviewConcurrencyPolicy.Effective(configured, parallelReviewExecutionEnabled: false));
    }

    // Raising the configured value is the obvious way to try to get parallelism back on an unlicensed host,
    // so the ceiling has to be a clamp rather than a default.
    [Fact]
    public void WithoutTheCapability_RaisingTheConfiguredWidthChangesNothing()
    {
        var low = ReviewConcurrencyPolicy.Effective(2, parallelReviewExecutionEnabled: false);
        var high = ReviewConcurrencyPolicy.Effective(10, parallelReviewExecutionEnabled: false);

        Assert.Equal(low, high);
        Assert.Equal(ReviewConcurrencyPolicy.Unlicensed, high);
    }
}
