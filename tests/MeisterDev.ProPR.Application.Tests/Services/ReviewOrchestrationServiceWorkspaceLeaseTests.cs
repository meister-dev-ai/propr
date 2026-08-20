// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Services;

/// <summary>
///     Every way out of the review pipeline has to release the workspace. A lease that is not released pins
///     its mirror and its checkout as "in use" for the rest of the process lifetime: cleanup skips them, the
///     disk keeps filling, and only a restart clears it.
/// </summary>
public partial class ReviewOrchestrationServiceTests
{
    [Fact]
    public async Task ProcessAsync_WhenTheJobIsCancelledBeforeFileReview_StillReleasesTheWorkspace()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository,
                _, _, logger) =
            CreateDeps();

        var job = CreateJob();
        var pr = CreatePullRequest();
        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<ReviewRevision?>(),
                Arg.Any<IReviewRepositoryWorkspace?>())
            .Returns(pr);

        // A stop that landed on another instance: this instance sees it in the persisted status at the
        // checkpoint before file review, and returns without reviewing anything.
        var cancelledJob = new ReviewJob(
            job.Id,
            job.ClientId,
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.PullRequestId,
            job.IterationId);
        cancelledJob.Status = JobStatus.Cancelled;
        jobs.GetById(job.Id).Returns(cancelledJob);

        var (workspaceManager, workspace) = CreateWorkspaceManagerWithSpy();
        var sut = CreateService(
            jobs,
            prFetcher,
            orchestrator,
            commentPoster,
            reviewerManager,
            clientRegistry,
            prScanRepository,
            logger,
            workspaceManager: workspaceManager);

        await sut.ProcessAsync(job, CancellationToken.None);

        // The fifth argument is matched explicitly: the four-argument form compiles as a call with a null
        // chat client, and the pipeline passes a non-null override, so the assertion would hold whether or
        // not file review ran.
        await orchestrator.DidNotReceive()
            .ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>());
        Assert.Equal(1, workspace.DisposeCount);
    }

    [Fact]
    public async Task ProcessAsync_WhenTheReviewIsPublished_ReleasesTheWorkspaceOnDispatchAndOnTheWayOut()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository,
                _, _, logger) =
            CreateDeps();

        var job = CreateJob();
        var pr = CreatePullRequest(new List<PrCommentThread>());
        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<ReviewRevision?>(),
                Arg.Any<IReviewRepositoryWorkspace?>())
            .Returns(pr);
        orchestrator.ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(Task.FromResult(new ReviewResult("A thorough review summary.", new List<ReviewComment>().AsReadOnly())));

        var (workspaceManager, workspace) = CreateWorkspaceManagerWithSpy();
        var sut = CreateService(
            jobs,
            prFetcher,
            orchestrator,
            commentPoster,
            reviewerManager,
            clientRegistry,
            prScanRepository,
            logger,
            workspaceManager: workspaceManager);

        await sut.ProcessAsync(job, CancellationToken.None);

        await AssertReviewPublishedAsync(commentPoster, job);

        // The dispatch path releases the workspace when file review ends, and the pipeline releases it again
        // on the way out, so disposal is called more than once on this path. Being called twice is the reason
        // the real workspace guards it.
        Assert.True(workspace.DisposeCount > 1, "the published path disposes on dispatch and on the way out");
    }

    /// <summary>
    ///     A throw before file review is the exit only the enclosing try/finally covers. The dispatch path
    ///     releases the workspace in its own finally, so a throw from file review itself would be released
    ///     with or without the one here and would prove nothing about it.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_WhenBuildingTheReviewContextThrows_StillReleasesTheWorkspace()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository,
                _, _, logger) =
            CreateDeps();

        var job = CreateJob();
        var pr = CreatePullRequest(new List<PrCommentThread>());
        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<ReviewRevision?>(),
                Arg.Any<IReviewRepositoryWorkspace?>())
            .Returns(pr);
        // Building the review context is where this fails, which is before anything is dispatched.
        var toolsFactory = Substitute.For<IReviewContextToolsFactory>();
        toolsFactory.Create(Arg.Any<ReviewContextToolsRequest>())
            .Returns(_ => throw new InvalidOperationException("the context tools could not be built"));

        var (workspaceManager, workspace) = CreateWorkspaceManagerWithSpy();
        var sut = CreateService(
            jobs,
            prFetcher,
            orchestrator,
            commentPoster,
            reviewerManager,
            clientRegistry,
            prScanRepository,
            logger,
            reviewContextToolsFactory: toolsFactory,
            workspaceManager: workspaceManager);

        await sut.ProcessAsync(job, CancellationToken.None);

        // The throw happened where this test says it does, and nothing was dispatched, so the release can
        // only have come from the pipeline's own finally.
        toolsFactory.Received(1).Create(Arg.Any<ReviewContextToolsRequest>());
        await orchestrator.DidNotReceive()
            .ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>());
        Assert.Equal(1, workspace.DisposeCount);
    }

    private static (IReviewRepositoryWorkspaceManager Manager, CountingWorkspace Workspace)
        CreateWorkspaceManagerWithSpy()
    {
        var workspace = new CountingWorkspace();
        var manager = Substitute.For<IReviewRepositoryWorkspaceManager>();
        manager.PrepareAsync(Arg.Any<ReviewRepositoryWorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewRepositoryWorkspacePreparationResult(workspace, null));
        return (manager, workspace);
    }

    /// <summary>
    ///     Counts how many times the pipeline asked for the workspace to be released.
    /// </summary>
    /// <remarks>
    ///     It counted the releases behind those calls as well, under the same guard the real workspace
    ///     applies, so the count was one whatever the pipeline did and asserting it tested this class. That
    ///     the guard holds is asserted against the real workspace in
    ///     <c>GitReviewRepositoryWorkspaceDisposalTests</c>; what is left to assert here is that the pipeline
    ///     asks at all, on every way out.
    /// </remarks>
    private sealed class CountingWorkspace : IReviewRepositoryWorkspace
    {
        public int DisposeCount { get; private set; }

        public ReviewRepositoryWorkspaceLease Lease { get; } = new(
            Guid.NewGuid(),
            "workspace-key",
            "/tmp/mirror",
            "/tmp/source",
            "head-sha",
            "base-sha",
            "merge-base-sha",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "Active");

        public Task<IReadOnlyList<ChangedFileSummary>> GetChangedFilesAsync(CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<ChangedFileSummary>>([]);
        }

        public Task<IReadOnlyList<string>> GetFileTreeAsync(string branchSide, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<string>>([]);
        }

        public Task<string?> ReadFileAsync(string path, string branchSide, CancellationToken ct)
        {
            return Task.FromResult<string?>(null);
        }

        public Task<string?> GetUnifiedDiffAsync(string path, CancellationToken ct)
        {
            return Task.FromResult<string?>(null);
        }

        public ValueTask DisposeAsync()
        {
            this.DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
