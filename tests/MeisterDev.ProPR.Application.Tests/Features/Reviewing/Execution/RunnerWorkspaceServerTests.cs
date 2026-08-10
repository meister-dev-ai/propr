// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Execution;

public sealed class RunnerWorkspaceServerTests
{
    private const long OneGibibyte = 1024L * 1024 * 1024;

    private static readonly Guid JobId = Guid.Parse("14141414-1414-1414-1414-141414141414");
    private static readonly RunnerCallContext Call = new(JobId, 2, "runner-a");

    private readonly IRunnerCallAuthorizer _authorizer = Substitute.For<IRunnerCallAuthorizer>();
    private readonly IRunnerWorkspaceRegistry _registry = new RunnerWorkspaceRegistry();
    private readonly IRunnerWorkspaceSizeProbe _probe = Substitute.For<IRunnerWorkspaceSizeProbe>();

    private RunnerWorkspaceServer CreateServer(
        bool authorized = true,
        bool held = true,
        long measuredBytes = 1024,
        long ceiling = OneGibibyte)
    {
        this._authorizer.AuthorizeAsync(Arg.Any<RunnerCallContext>(), Arg.Any<CancellationToken>())
            .Returns(
                authorized
                    ? RunnerCallAuthorization.Allow(Guid.NewGuid())
                    : RunnerCallAuthorization.Refuse(RunnerCallRefusal.SupersededGeneration));
        this._probe.MeasureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(measuredBytes);

        if (held)
        {
            // Completes synchronously: nothing is being replaced, so there is nothing to dispose.
            this._registry.RegisterAsync(
                    JobId,
                    new RunnerWorkspaceSource("/var/propr/mirrors/abc", "head-sha", "base-sha", ceiling))
                .GetAwaiter()
                .GetResult();
        }

        return new RunnerWorkspaceServer(this._authorizer, this._registry, this._probe);
    }

    [Fact]
    public async Task AnAuthorizedFetch_IsGrantedTheJobsMirrorAtItsPinnedCommits()
    {
        var grant = await this.CreateServer().AuthorizeFetchAsync(Call);

        Assert.True(grant.IsGranted);
        Assert.Equal("/var/propr/mirrors/abc", grant.Source!.MirrorPath);
        Assert.Equal("head-sha", grant.Source.HeadSha);
        Assert.Equal("base-sha", grant.Source.BaseSha);
    }

    // Repository content is the largest thing an executor can ask for, so it is the last place to make an
    // exception for a caller that no longer holds the job.
    [Fact]
    public async Task AnUnauthorizedFetch_IsRefusedWithoutSoMuchAsMeasuringTheMirror()
    {
        var grant = await this.CreateServer(authorized: false).AuthorizeFetchAsync(Call);

        Assert.False(grant.IsGranted);
        Assert.Equal(RunnerWorkspaceRefusal.NotAuthorized, grant.Refusal);
        Assert.Equal(RunnerCallRefusal.SupersededGeneration, grant.CallRefusal);
        await this._probe.DidNotReceive().MeasureAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // A ceiling checked while streaming is not a ceiling: by the time it trips, the egress is paid for.
    [Fact]
    public async Task ContentLargerThanTheCeiling_IsRefusedBeforeAnythingIsSent()
    {
        var grant = await this.CreateServer(measuredBytes: 5L * OneGibibyte, ceiling: OneGibibyte)
            .AuthorizeFetchAsync(Call);

        Assert.False(grant.IsGranted);
        Assert.Equal(RunnerWorkspaceRefusal.ExceedsSizeCeiling, grant.Refusal);
    }

    // The refusal has to tell an operator what happened and what to do, not just that something failed.
    [Fact]
    public async Task TheCeilingRefusal_NamesBothSizesAndWhatToDo()
    {
        var grant = await this.CreateServer(measuredBytes: 5L * OneGibibyte, ceiling: OneGibibyte)
            .AuthorizeFetchAsync(Call);

        Assert.Contains("5120 MiB", grant.Reason!, StringComparison.Ordinal);
        Assert.Contains("1024 MiB", grant.Reason!, StringComparison.Ordinal);
        Assert.Contains("Raise the ceiling", grant.Reason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContentExactlyAtTheCeiling_IsStillServed()
    {
        var grant = await this.CreateServer(measuredBytes: OneGibibyte, ceiling: OneGibibyte)
            .AuthorizeFetchAsync(Call);

        Assert.True(grant.IsGranted);
    }

    [Fact]
    public async Task WithNoMirrorHeld_TheFetchIsRefusedWithAReadableReason()
    {
        var grant = await this.CreateServer(held: false).AuthorizeFetchAsync(Call);

        Assert.False(grant.IsGranted);
        Assert.Equal(RunnerWorkspaceRefusal.NoMirrorHeld, grant.Refusal);
        Assert.NotNull(grant.Reason);
    }

    // Job-scoped content is released when the job ends; a mirror still on offer afterwards is content
    // being served for work nobody is doing.
    [Fact]
    public async Task ReleasingAJob_StopsItsContentBeingServed()
    {
        var server = this.CreateServer();
        Assert.True((await server.AuthorizeFetchAsync(Call)).IsGranted);

        await this._registry.ReleaseAsync(JobId);

        Assert.Equal(RunnerWorkspaceRefusal.NoMirrorHeld, (await server.AuthorizeFetchAsync(Call)).Refusal);
    }
}
