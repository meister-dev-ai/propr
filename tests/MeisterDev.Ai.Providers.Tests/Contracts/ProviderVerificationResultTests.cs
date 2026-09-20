// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Tests.Contracts;

/// <summary>
///     The one invariant a verification outcome carries: the status and the failure category are one fact.
/// </summary>
public sealed class ProviderVerificationResultTests
{
    // A failure with no category reaches an operator as "it did not work" and nothing to act on, and the console
    // branches on the category to say what to change.
    [Fact]
    public void AFailedVerificationWithNoCategoryIsRefused()
    {
        Assert.Throws<ArgumentException>(() => new ProviderVerificationResult(AiVerificationStatus.Failed));
    }

    // The console reads the category to describe a problem, so one on a result that succeeded is shown as a
    // problem the connection does not have.
    [Theory]
    [InlineData(AiVerificationStatus.Verified)]
    [InlineData(AiVerificationStatus.NeverVerified)]
    public void AVerificationThatDidNotFailCarriesNoCategory(AiVerificationStatus status)
    {
        Assert.Throws<ArgumentException>(() => new ProviderVerificationResult(status, AiVerificationFailureCategory.Credentials));
    }

    [Fact]
    public void TheTwoStatedTogetherAreAccepted()
    {
        var failed = new ProviderVerificationResult(
            AiVerificationStatus.Failed,
            AiVerificationFailureCategory.Credentials,
            "The provider refused the key.");

        Assert.Equal(AiVerificationFailureCategory.Credentials, failed.FailureCategory);
        Assert.Equal(AiVerificationStatus.Verified, new ProviderVerificationResult(AiVerificationStatus.Verified).Status);
        Assert.Equal(AiVerificationStatus.NeverVerified, ProviderVerificationResult.NeverVerified.Status);
    }

    // A copy that moves a verified result to failed without stating why is the same defect by another route.
    [Fact]
    public void ACopyThatFailsWithoutACategoryIsRefused()
    {
        var verified = new ProviderVerificationResult(AiVerificationStatus.Verified);

        Assert.Throws<ArgumentException>(() => verified with { Status = AiVerificationStatus.Failed });
    }
}
