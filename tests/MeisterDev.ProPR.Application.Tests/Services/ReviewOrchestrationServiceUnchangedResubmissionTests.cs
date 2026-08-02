// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Application.Support;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
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
        orchestrator.ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(CreateReviewResult());

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        await service.ProcessAsync(job, CancellationToken.None);

        await jobs.DidNotReceiveWithAnyArgs()
            .GetBestTerminalJobWithFileResultsByStoredRevisionAsync(
                default!,
                default!,
                default!,
                default,
                default!,
                default);
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
            .Returns(CreatePullRequest());
    }
}
