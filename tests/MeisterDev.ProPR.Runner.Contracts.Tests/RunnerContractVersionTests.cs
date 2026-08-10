// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Runner.Contracts;

namespace MeisterDev.ProPR.Runner.Contracts.Tests;

public sealed class RunnerContractVersionTests
{
    [Fact]
    public void TheCurrentVersion_IsServed()
    {
        Assert.True(RunnerContractVersion.IsSupported(RunnerContractVersion.Current));
    }

    // A control-plane deploy must not refuse every runner that has not been upgraded yet, which is what
    // turns a routine upgrade into a fleet outage.
    [Fact]
    public void OnePriorVersion_IsStillServed()
    {
        Assert.True(RunnerContractVersion.IsSupported(RunnerContractVersion.Oldest));
        Assert.True(RunnerContractVersion.Oldest <= RunnerContractVersion.Current);
    }

    [Fact]
    public void AVersionOutsideTheWindow_IsRefused()
    {
        Assert.False(RunnerContractVersion.IsSupported(RunnerContractVersion.Current + 1));
        Assert.False(RunnerContractVersion.IsSupported(RunnerContractVersion.Oldest - 1));
    }

    // An operator reading the refusal has to learn which side to move, so it names both.
    [Fact]
    public void TheRefusal_NamesTheReportedAndTheSupportedVersions()
    {
        var reported = RunnerContractVersion.Current + 3;

        var message = RunnerContractVersion.DescribeMismatch(reported);

        Assert.Contains(reported.ToString(), message, StringComparison.Ordinal);
        Assert.Contains(RunnerContractVersion.Current.ToString(), message, StringComparison.Ordinal);
        Assert.Contains(RunnerContractVersion.Oldest.ToString(), message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRefusal_SaysWhichSideIsOlder()
    {
        Assert.Contains(
            "upgrade the control plane",
            RunnerContractVersion.DescribeMismatch(RunnerContractVersion.Current + 1),
            StringComparison.Ordinal);
        Assert.Contains(
            "upgrade the runner image",
            RunnerContractVersion.DescribeMismatch(RunnerContractVersion.Oldest - 1),
            StringComparison.Ordinal);
    }

    // A version that cannot read this build's manifests cannot usefully be served anywhere, so the floor
    // clamps the whole window. An offer that refuses a version the heartbeat calls healthy is two answers
    // to one question, and an operator chasing the wrong one.
    [Fact]
    public void TheServedWindow_IsClampedByTheManifestFloor()
    {
        Assert.True(RunnerContractVersion.Oldest >= RunnerContractVersion.OldestManifestCompatible);
        Assert.False(RunnerContractVersion.IsSupported(RunnerContractVersion.OldestManifestCompatible - 1));
    }

    // "Too old" alone reads as an ordinary window miss; a shape change is a different instruction to the
    // operator, so the refusal says which one it was.
    [Fact]
    public void ARefusalBelowTheManifestFloor_NamesTheShapeChange()
    {
        var message = RunnerContractVersion.DescribeMismatch(RunnerContractVersion.OldestManifestCompatible - 1);

        Assert.Contains("manifest shape changed", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTypedError_CarriesTheStableCodeAndTheDiagnostic()
    {
        var error = RunnerContractError.ForUnsupportedVersion(99);

        Assert.Equal(RunnerContractError.UnsupportedContractVersion, error.Code);
        Assert.Contains("99", error.Message, StringComparison.Ordinal);
    }
}
