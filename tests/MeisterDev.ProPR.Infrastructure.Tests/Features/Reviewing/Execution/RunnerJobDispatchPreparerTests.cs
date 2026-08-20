// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution;

/// <summary>
///     Dispatch has to hand the runner two things this replica will be asked for later: the workspace it
///     fetches from, and the tools it calls back through. The tools were registered nowhere, so every proxied
///     call missed the registry and came back refused, and refused as a lost lease, which pointed the reader
///     at the lease machinery for a problem that was not there.
/// </summary>
public sealed class RunnerJobDispatchPreparerTests
{
    private static readonly Guid JobId = Guid.Parse("11111111-1111-4111-8111-111111111111");

    private readonly IReviewRepositoryWorkspaceManager _workspaces =
        Substitute.For<IReviewRepositoryWorkspaceManager>();

    private readonly IReviewContextToolsFactory _toolsFactory = Substitute.For<IReviewContextToolsFactory>();
    private readonly RunnerWorkspaceRegistry _workspaceRegistry = new();
    private readonly RunnerJobToolsRegistry _toolsRegistry = new();
    private readonly IPullRequestFetcher _pullRequests = Substitute.For<IPullRequestFetcher>();

    [Fact]
    public async Task PreparingAJob_RegistersTheToolsTheRunnerWillCallBackThrough()
    {
        var job = MakeJob();
        this.GivenAPreparedWorkspace();
        var tools = Substitute.For<IReviewContextTools>();
        this._toolsFactory.Create(Arg.Any<ReviewContextToolsRequest>()).Returns(tools);

        var preparation = await this.CreatePreparer().PrepareAsync(job, MakeLease());

        Assert.True(preparation.Succeeded);
        var held = this._toolsRegistry.Find(JobId);
        Assert.NotNull(held);
        Assert.Same(tools, held!.Tools);
    }

    // The same request the in-process path builds, so a proxied call answers from the same context rather
    // than from a differently-shaped one.
    [Fact]
    public async Task TheRegisteredTools_AreBuiltForTheJobUnderReview()
    {
        var job = MakeJob();
        this.GivenAPreparedWorkspace();
        this._toolsFactory.Create(Arg.Any<ReviewContextToolsRequest>())
            .Returns(Substitute.For<IReviewContextTools>());

        await this.CreatePreparer().PrepareAsync(job, MakeLease());

        this._toolsFactory.Received(1).Create(
            Arg.Is<ReviewContextToolsRequest>(request =>
                request.CodeReview.Number == job.PullRequestId
                && request.ClientId == job.ClientId
                && request.SourceBranch == "feature"
                && request.TargetBranch == "main"
                && request.Workspace != null));
    }

    // Claiming a knowledge surface the installation does not have is the failure this guards: an executor
    // told "nothing found" records that as evidence, where "not offered" is not evidence of anything.
    [Fact]
    public async Task WithNoProCursorGateway_CodeKnowledgeIsNotClaimedAsOffered()
    {
        this.GivenAPreparedWorkspace();
        this._toolsFactory.Create(Arg.Any<ReviewContextToolsRequest>())
            .Returns(Substitute.For<IReviewContextTools>());

        await this.CreatePreparer().PrepareAsync(MakeJob(), MakeLease());

        Assert.False(this._toolsRegistry.Find(JobId)!.CodeKnowledgeOffered);
    }

    // A job that cannot be prepared must leave nothing registered behind it.
    [Fact]
    public async Task AJobWithNoResolvedRevision_RegistersNothing()
    {
        this.GivenAPreparedWorkspace();
        var job = new ReviewJob(JobId, Guid.NewGuid(), "https://forge.invalid/org", "project", "repo", 42, 1);

        var preparation = await this.CreatePreparer().PrepareAsync(job, MakeLease());

        Assert.False(preparation.Succeeded);
        Assert.Null(this._toolsRegistry.Find(JobId));
    }

    // A resumed job leased to a runner used to re-pay everything: the adoption the in-process path
    // applies at review start happened nowhere on the dispatch path, so the executor's prior-results
    // read found nothing to skip.
    [Fact]
    public async Task PreparingAJob_AdoptsAPriorAttemptsFinishedFilesBeforeTheManifestLeaves()
    {
        var job = MakeJob();
        this.GivenAPreparedWorkspace(changedPaths: ["src/a.cs", "src/b.cs"]);
        this._toolsFactory.Create(Arg.Any<ReviewContextToolsRequest>()).Returns(Substitute.For<IReviewContextTools>());

        var priorAttempt = new ReviewJob(Guid.NewGuid(), job.ClientId, job.OrganizationUrl, "project", "repo", 42, 1);
        var finished = new ReviewFileResult(priorAttempt.Id, "src/a.cs");
        finished.MarkCompleted("looks fine", [], ["pass-1"]);
        priorAttempt.FileReviewResults.Add(finished);

        var executionStore = Substitute.For<IReviewJobExecutionStore>();
        executionStore.GetBestTerminalJobWithFileResultsByStoredRevisionAsync(
                job.OrganizationUrl, "project", "repo", 42, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(priorAttempt);
        var scans = Substitute.For<IReviewPrScanWatermarkStore>();
        var priorRows = Substitute.For<IReviewFileResultStore>();
        priorRows.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        var preparation = await this.CreatePreparer(
                reuse: new ReviewJobReuse(executionStore, scans, NullLogger.Instance),
                priorRows: priorRows)
            .PrepareAsync(job, MakeLease());

        Assert.True(preparation.Succeeded);
        await executionStore.Received(1).AddFileResultAsync(
            Arg.Is<ReviewFileResult>(row => row.FilePath == "src/a.cs" && row.IsComplete && !row.IsCarriedForward),
            Arg.Any<CancellationToken>());
    }

    // Adoption happens once. A job re-dispatched after a lost claim already has its rows, and writing
    // them again would violate the one-row-per-file invariant the store enforces.
    [Fact]
    public async Task AJobThatAlreadyHasRows_AdoptsNothingASecondTime()
    {
        var job = MakeJob();
        this.GivenAPreparedWorkspace(changedPaths: ["src/a.cs"]);
        this._toolsFactory.Create(Arg.Any<ReviewContextToolsRequest>()).Returns(Substitute.For<IReviewContextTools>());

        var alreadyAdopted = new ReviewJob(JobId, job.ClientId, job.OrganizationUrl, "project", "repo", 42, 1);
        var row = new ReviewFileResult(JobId, "src/a.cs");
        row.MarkCompleted("done", [], []);
        alreadyAdopted.FileReviewResults.Add(row);

        var executionStore = Substitute.For<IReviewJobExecutionStore>();
        var scans = Substitute.For<IReviewPrScanWatermarkStore>();
        var priorRows = Substitute.For<IReviewFileResultStore>();
        priorRows.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>()).Returns(alreadyAdopted);

        await this.CreatePreparer(
                reuse: new ReviewJobReuse(executionStore, scans, NullLogger.Instance),
                priorRows: priorRows)
            .PrepareAsync(job, MakeLease());

        await executionStore.DidNotReceiveWithAnyArgs().AddFileResultAsync(default!, default);
    }

    private static ReviewJob MakeJob()
    {
        var job = new ReviewJob(JobId, Guid.NewGuid(), "https://forge.invalid/org", "project", "repo", 42, 1);
        job.SetReviewRevision(new ReviewRevision("head-sha", "base-sha", null, "rev-1", "base-sha...head-sha"));
        return job;
    }

    private static ReviewJobLease MakeLease()
    {
        return new ReviewJobLease(JobId, "runner-1", 1, DateTimeOffset.UtcNow.AddMinutes(5));
    }

    private IReviewRepositoryWorkspace? _preparedWorkspace;

    // The workspace has to outlive the offer, because the runner fetches from its mirror throughout its
    // execution, and still be released when the job leaves this replica. The registry owns it for that whole
    // span. A preparer that let it fall out of scope instead kept two checkouts on disk per job.
    [Fact]
    public async Task ThePreparedWorkspace_IsOwnedByTheRegistryAndDisposedWithItsRelease()
    {
        var job = MakeJob();
        this.GivenAPreparedWorkspace();
        this._toolsFactory.Create(Arg.Any<ReviewContextToolsRequest>()).Returns(Substitute.For<IReviewContextTools>());

        await this.CreatePreparer().PrepareAsync(job, MakeLease());
        Assert.NotNull(this._workspaceRegistry.Find(JobId));
        await this._preparedWorkspace!.DidNotReceive().DisposeAsync();

        await this._workspaceRegistry.ReleaseAsync(JobId);

        Assert.Null(this._workspaceRegistry.Find(JobId));
        await this._preparedWorkspace!.Received(1).DisposeAsync();
    }

    private void GivenAPreparedWorkspace(IReadOnlyList<string>? changedPaths = null)
    {
        this._pullRequests
            .FetchRefAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PullRequestRef("feature", "main", PrStatus.Active)));

        var workspace = Substitute.For<IReviewRepositoryWorkspace>();
        this._preparedWorkspace = workspace;
        workspace.Lease.Returns(
            new ReviewRepositoryWorkspaceLease(
                JobId,
                "key",
                "/mirror",
                "/head",
                "head-sha",
                "base-sha",
                "merge-base",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                "Ready"));
        workspace.GetChangedFilesAsync(Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<ChangedFileSummary>>([.. (changedPaths ?? []).Select(path => new ChangedFileSummary(path, ChangeType.Edit))]));

        this._workspaces
            .PrepareAsync(Arg.Any<ReviewRepositoryWorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ReviewRepositoryWorkspacePreparationResult(workspace, null)));
    }

    private RunnerJobDispatchPreparer CreatePreparer(
        ReviewJobReuse? reuse = null,
        IReviewFileResultStore? priorRows = null)
    {
        return new RunnerJobDispatchPreparer(
            this._workspaces,
            this._workspaceRegistry,
            this._toolsFactory,
            this._toolsRegistry,
            this._pullRequests,
            Microsoft.Extensions.Options.Options.Create(new ReviewWorkspaceOptions()),
            reuse: reuse,
            priorRows: priorRows);
    }
}
