// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.CodeAnalysis;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Workspace;
using MeisterDev.ProPR.ProRV.Abstractions;
using MeisterDev.ProPR.Runner.Contracts;
using MeisterDev.ProPR.Runner.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MeisterDev.ProPR.Runner.Tests;

/// <summary>
///     Whether a review records the model's reasoning is the tenant's decision, made on the control plane and
///     carried in the manifest. This host binds its own switch from its own environment, so what matters is
///     that the manifest wins over it.
/// </summary>
public sealed class ManifestReasoningCaptureTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task TheManifestsDecisionOverridesThisHostsOwnSwitch(bool stated, bool hostSwitch)
    {
        var context = await BuildContextAsync(Behaviour(stated), hostSwitch);

        Assert.Equal(stated, context.CaptureReasoning);
        Assert.Equal(stated, context.CapturesReasoning(new AiReviewOptions { CaptureReasoningInProtocol = hostSwitch }));
    }

    // A manifest from an older control plane states nothing, and this host's own switch decides, which is how
    // the review behaved before the field existed. The switch is read when the review runs, so the value the
    // context was built with is not the one asked about here.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AManifestThatStatesNothing_LeavesThisHostsOwnSwitchInCharge(bool hostSwitch, bool switchAtReviewTime)
    {
        var context = await BuildContextAsync(Behaviour(null), hostSwitch);

        Assert.Null(context.CaptureReasoning);
        Assert.Equal(
            switchAtReviewTime,
            context.CapturesReasoning(new AiReviewOptions { CaptureReasoningInProtocol = switchAtReviewTime }));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AManifestWithoutABehaviourSection_LeavesThisHostsOwnSwitchInCharge(bool hostSwitch, bool switchAtReviewTime)
    {
        var context = await BuildContextAsync(behaviour: null, hostSwitch: hostSwitch);

        Assert.Null(context.CaptureReasoning);
        Assert.Equal(
            switchAtReviewTime,
            context.CapturesReasoning(new AiReviewOptions { CaptureReasoningInProtocol = switchAtReviewTime }));
    }

    private static RunnerReviewBehaviour Behaviour(bool? captureReasoning)
    {
        return new RunnerReviewBehaviour(false, true, null, null, captureReasoning);
    }

    private static async Task<MeisterDev.ProPR.Application.ValueObjects.ReviewSystemContext> BuildContextAsync(
        RunnerReviewBehaviour? behaviour,
        bool hostSwitch)
    {
        var manifest = RunnerManifests.Sample() with { Behaviour = behaviour };
        var workspace = new EmptyWorkspace();
        var job = RunnerReviewSubject.BuildJob(manifest);
        var pullRequest = await RunnerReviewSubject.BuildPullRequestAsync(manifest, workspace, CancellationToken.None);

        var hostOptions = Options.Create(new RunnerHostOptions());
        var executor = new RunnerJobExecutor(
            Substitute.For<IHttpClientFactory>(),
            new WorkspaceFetcher(hostOptions, NullLogger<WorkspaceFetcher>.Instance),
            new RunnerCredentialStore(hostOptions),
            Options.Create(new AiReviewOptions { CaptureReasoningInProtocol = hostSwitch }),
            Options.Create(new ReviewWorkspaceOptions()),
            Substitute.For<IProRVPrefilter>(),
            Substitute.For<IStructuralCodeAnalyzer>(),
            TimeProvider.System,
            NullLoggerFactory.Instance,
            NullLogger<RunnerJobExecutor>.Instance);

        using var http = new HttpClient { BaseAddress = new Uri("https://control-plane.invalid") };

        return executor.BuildContext(
            manifest,
            job,
            pullRequest,
            workspace,
            http,
            Substitute.For<IProtocolRecorder>(),
            new RelayChatClient(http, manifest.JobId, manifest.LeaseGeneration, manifest.DefaultModel.LogicalModelName));
    }

    private sealed class EmptyWorkspace : IReviewRepositoryWorkspace
    {
        public ReviewRepositoryWorkspaceLease Lease { get; } = new(
            Guid.Empty, "key", "/mirror", "/source", "head", "base", "merge-base",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "Active");

        public Task<IReadOnlyList<ChangedFileSummary>> GetChangedFilesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ChangedFileSummary>>([]);

        public Task<IReadOnlyList<string>> GetFileTreeAsync(string branchSide, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> ReadFileAsync(string path, string branchSide, CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public Task<string?> GetUnifiedDiffAsync(string path, CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public Task<int> CountChangedLinesAsync(IReadOnlyCollection<string> paths, CancellationToken ct) =>
            Task.FromResult(0);

        public Task<long> CountDiffBytesAsync(IReadOnlyCollection<string> paths, CancellationToken ct) =>
            Task.FromResult(0L);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
