// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Support;

namespace MeisterDev.ProPR.Application.Tests.Support;

public sealed class AssemblyProductVersionProviderTests
{
    [Fact]
    public void AStampedRelease_IsReportedAsItWasStamped()
    {
        Assert.Equal("1.0.0.alpha.0049", AssemblyProductVersionProvider.Normalize("1.0.0.alpha.0049"));
    }

    // The SDK appends the commit sha when nothing sets a version. That sha identifies a single build rather
    // than a release, and would be the highest-entropy value in an otherwise anonymous payload.
    [Fact]
    public void ACommitShaSuffix_IsStrippedBeforeTheVersionIsReported()
    {
        Assert.Equal(
            "1.4.2",
            AssemblyProductVersionProvider.Normalize("1.4.2+1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b"));
    }

    // An unstamped build reports a placeholder rather than the SDK default, so a local build is not counted as
    // release 1.0.0 in a support conversation or in the fleet-wide version spread.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1.0.0")]
    [InlineData("1.0.0+1a2b3c4d")]
    public void AnUnstampedBuild_ReportsThePlaceholder(string? informationalVersion)
    {
        Assert.Equal(
            AssemblyProductVersionProvider.UnstampedVersion,
            AssemblyProductVersionProvider.Normalize(informationalVersion));
    }

    [Fact]
    public void TheProvider_ReadsTheVersionFromTheAssemblyItIsGiven()
    {
        var provider = new AssemblyProductVersionProvider(typeof(AssemblyProductVersionProviderTests).Assembly);

        Assert.False(string.IsNullOrWhiteSpace(provider.Version));
    }
}
