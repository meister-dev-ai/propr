// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Application.Support;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Services;

/// <summary>
///     Covers the execution-side copy of the "nothing has changed here" rule. It is applied twice, once when a
///     job is queued and once when it runs, and the two have to agree: this one deletes the job rather than
///     recording a skip, so a review that passes intake and fails here disappears with nothing said.
/// </summary>
public partial class ReviewOrchestrationServiceTests
{
    [Fact]
    public async Task ProcessAsync_UnchangedRevisionWithoutAnExplicitRequest_SkipsAndDeletesTheJob()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _, logger) =
            CreateDeps();

        var job = CreateJob();
        StubUnchangedRevision(prScanRepository, prFetcher, job);

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        await service.ProcessAsync(job, CancellationToken.None);

        await jobs.Received(1).DeleteAsync(job.Id, Arg.Any<CancellationToken>());
        await orchestrator.DidNotReceiveWithAnyArgs()
            .ReviewAsync(default!, default!, default!, default, default);
    }

    [Fact]
    public async Task ProcessAsync_UnchangedRevisionAskedForExplicitly_ReviewsInsteadOfDeletingTheJob()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _, logger) =
            CreateDeps();

        var job = CreateJob();
        job.SetAllowUnchangedResubmission(true);
        StubUnchangedRevision(prScanRepository, prFetcher, job);
        orchestrator.ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(CreateReviewResult());

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        await service.ProcessAsync(job, CancellationToken.None);

        await jobs.DidNotReceive().DeleteAsync(job.Id, Arg.Any<CancellationToken>());
        await orchestrator.Received(1).ReviewAsync(
            Arg.Is<ReviewJob>(reviewed => reviewed.Id == job.Id),
            Arg.Any<PullRequest>(),
            Arg.Any<ReviewSystemContext>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IChatClient?>());
    }

    [Fact]
    public async Task ProcessAsync_UnchangedRevisionAskedForExplicitly_ReviewsTheFullCurrentScope()
    {
        // A revision that is not new has no delta to compare against and no carry-forward baseline, so the
        // fetch is already the whole pull request. Asserted because the alternative would be a re-review
        // that fetches nothing, finds nothing, and reports success.
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _, logger) =
            CreateDeps();

        var job = CreateJob();
        job.SetAllowUnchangedResubmission(true);
        StubUnchangedRevision(prScanRepository, prFetcher, job);
        orchestrator.ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(CreateReviewResult());

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        await service.ProcessAsync(job, CancellationToken.None);

        await prFetcher.Received(1).FetchAsync(
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.PullRequestId,
            job.IterationId,
            null,
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>(),
            null,
            Arg.Any<IReviewRepositoryWorkspace?>());
    }

    [Fact]
    public async Task ProcessAsync_UnchangedRevisionAskedForExplicitly_DoesNotAdoptThePriorReviewsFileResults()
    {
        // Resume adopts a prior job's finished files for the same revision so interrupted work is not redone.
        // Here the prior job is the completed review itself, so adopting it would leave every file already
        // done and nothing to review: the request would report success having reviewed nothing.
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _, logger) =
            CreateDeps();

        var job = CreateJob();
        job.SetAllowUnchangedResubmission(true);
        StubUnchangedRevision(prScanRepository, prFetcher, job);
        StubPriorJobToResumeFrom(jobs, job, budgetBlocked: false, inScopeChangedFileCount: 1);
        orchestrator.ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(CreateReviewResult());

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        await service.ProcessAsync(job, CancellationToken.None);

        // Asserted on what was adopted rather than on whether the lookup happened. Whether a prior run stopped
        // short at a budget cap cannot be known without looking it up, so the query is no longer the signal;
        // adopting its files is.
        await jobs.DidNotReceive().AddFileResultAsync(
            Arg.Is<ReviewFileResult>(result => result.ResumedFromJobId != null),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A run stopped by a budget cap records the revision as processed while having reviewed only part of
    ///     it. Standing resume down there leaves no way to finish the review except paying again for every file
    ///     the capped run already covered.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_PriorRunStoppedShortAtABudgetCap_AdoptsWhatItAlreadyReviewed()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _, logger) =
            CreateDeps();

        var job = CreateJob();
        job.SetAllowUnchangedResubmission(true);
        StubUnchangedRevision(prScanRepository, prFetcher, job);
        StubPriorJobToResumeFrom(jobs, job, budgetBlocked: true, inScopeChangedFileCount: 275);
        orchestrator.ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(CreateReviewResult());

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        await service.ProcessAsync(job, CancellationToken.None);

        await jobs.Received().AddFileResultAsync(
            Arg.Is<ReviewFileResult>(result => result.ResumedFromJobId != null),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A cap that tripped on the last file left nothing outstanding. Adopting that run wholesale would
    ///     answer an explicit re-review request with no fresh work at all, which is the case resume stands
    ///     down for.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_PriorRunHitACapButReviewedEverything_DoesNotAdoptIt()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _, logger) =
            CreateDeps();

        var job = CreateJob();
        job.SetAllowUnchangedResubmission(true);
        StubUnchangedRevision(prScanRepository, prFetcher, job);
        StubPriorJobToResumeFrom(jobs, job, budgetBlocked: true, inScopeChangedFileCount: 1);
        orchestrator.ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(CreateReviewResult());

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        await service.ProcessAsync(job, CancellationToken.None);

        await jobs.DidNotReceive().AddFileResultAsync(
            Arg.Is<ReviewFileResult>(result => result.ResumedFromJobId != null),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Stubs a terminal prior job at the same revision carrying one finished file result, so the resume
    ///     decision has something real to adopt or decline.
    /// </summary>
    private static void StubPriorJobToResumeFrom(
        IReviewJobExecutionStore jobs,
        ReviewJob job,
        bool budgetBlocked,
        int inScopeChangedFileCount)
    {
        var prior = new ReviewJob(
            Guid.NewGuid(),
            job.ClientId,
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.PullRequestId,
            job.IterationId);
        prior.SetReviewRevision(job.ReviewRevisionReference!);
        prior.Status = JobStatus.Completed;
        prior.SetInScopeChangedFileCount(inScopeChangedFileCount);

        if (budgetBlocked)
        {
            prior.SetBudgetBlock(BudgetScopeKind.Increment, BudgetCapKind.Soft, 5m, 5.12m);
        }

        var priorResult = new ReviewFileResult(prior.Id, ResumableFilePath);
        priorResult.MarkCompleted("Already looked at.", []);
        prior.FileReviewResults.Add(priorResult);

        jobs.GetBestTerminalJobWithFileResultsByStoredRevisionAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(prior);
    }

    [Fact]
    public async Task ProcessAsync_UnchangedRevisionWithoutAnExplicitRequest_StillLooksForWorkToResume()
    {
        // The companion to the test above: standing resume behaviour is untouched for every other job.
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _, logger) =
            CreateDeps();

        var job = CreateJob();
        StubUnchangedRevision(prScanRepository, prFetcher, job);

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        await service.ProcessAsync(job, CancellationToken.None);

        await jobs.Received(1).GetBestTerminalJobWithFileResultsByStoredRevisionAsync(
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.PullRequestId,
            "1",
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Puts the pull request in the state both guards react to: a scan that already records this exact
    ///     revision, and no reviewer threads carrying a new reply.
    /// </summary>
    /// <summary>A path present both in the prior job's finished work and in the current change set.</summary>
    private const string ResumableFilePath = "src/Reviewed.cs";

    private static void StubUnchangedRevision(
        IReviewPrScanRepository prScanRepository,
        IPullRequestFetcher prFetcher,
        ReviewJob job)
    {
        var scan = new ReviewPrScan(
            Guid.NewGuid(),
            job.ClientId,
            job.RepositoryId,
            job.PullRequestId,
            ReviewRevisionKeys.GetStoredKey(job.ReviewRevisionReference, job.IterationId));

        prScanRepository
            .GetAsync(job.ClientId, job.RepositoryId, job.PullRequestId, Arg.Any<CancellationToken>())
            .Returns(scan);

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
            .Returns(
                CreatePullRequest() with
                {
                    ChangedFiles = new List<ChangedFile>
                    {
                        new(ResumableFilePath, ChangeType.Edit, "content", "+line"),
                    }.AsReadOnly(),
                });
    }
}
