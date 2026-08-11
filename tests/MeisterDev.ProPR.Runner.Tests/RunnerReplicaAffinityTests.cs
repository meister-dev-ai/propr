// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Runner.Execution;

namespace MeisterDev.ProPR.Runner.Tests;

/// <summary>
///     Where a job's calls go. The rule that matters: an advertised replica address routes everything
///     job-scoped, an absent one changes nothing, and an insecure one is refused. The credential is sent on
///     every call, so there is no partial-https case.
/// </summary>
public sealed class RunnerReplicaAffinityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoAdvertisedAddress_IsValidAndResolvesRelative(string? servedBy)
    {
        Assert.True(RunnerReplicaAffinity.TryValidate(servedBy, out var error));
        Assert.Null(error);

        var resolved = RunnerReplicaAffinity.Resolve(servedBy, "runners/lease/heartbeat");
        Assert.False(resolved.IsAbsoluteUri);
    }

    [Theory]
    [InlineData("https://replica-2.internal")]
    [InlineData("https://replica-2.internal:8443/")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://localhost:5000")]
    public void ASecureOrLoopbackAddress_IsAccepted(string servedBy)
    {
        Assert.True(RunnerReplicaAffinity.TryValidate(servedBy, out _));
    }

    [Theory]
    [InlineData("http://replica-2.internal:8080")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    public void AnInsecureOrMalformedAddress_IsRefusedWithAReason(string servedBy)
    {
        Assert.False(RunnerReplicaAffinity.TryValidate(servedBy, out var error));
        Assert.Contains(servedBy, error!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAdvertisedAddress_ResolvesAbsoluteAgainstTheReplica()
    {
        var resolved = RunnerReplicaAffinity.Resolve("https://replica-2.internal:8443", "runners/execution/");

        Assert.Equal("https://replica-2.internal:8443/runners/execution/", resolved.ToString());
    }

    // Trailing slashes are how base-URL bugs are born; both spellings must land on the same address.
    [Theory]
    [InlineData("https://replica-2.internal")]
    [InlineData("https://replica-2.internal/")]
    public void ATrailingSlash_ChangesNothing(string servedBy)
    {
        var resolved = RunnerReplicaAffinity.Resolve(servedBy, "runners/lease/release");

        Assert.Equal("https://replica-2.internal/runners/lease/release", resolved.ToString());
    }

    [Fact]
    public void ACallerWithNoBaseOfItsOwn_FallsBackToTheConfiguredControlPlane()
    {
        var resolved = RunnerReplicaAffinity.ResolveAbsolute(null, "https://control-plane.internal", "runners/execution/workspace/x/1");

        Assert.Equal("https://control-plane.internal/runners/execution/workspace/x/1", resolved.ToString());
    }

    [Fact]
    public void ACallerWithNoBaseOfItsOwn_PrefersTheGrantingReplica()
    {
        var resolved = RunnerReplicaAffinity.ResolveAbsolute(
            "https://replica-2.internal",
            "https://control-plane.internal",
            "runners/execution/workspace/x/1");

        Assert.Equal("https://replica-2.internal/runners/execution/workspace/x/1", resolved.ToString());
    }
}
