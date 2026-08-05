// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Services;

/// <summary>
///     A file review reviews files. Thread work lives in the thread pass, and the review path neither writes
///     into a thread nor advances the counters that record having done so.
/// </summary>
public partial class ReviewOrchestrationServiceTests
{
    [Fact]
    public async Task ProcessAsync_ReviewerOwnedThreadPresent_PostsNoReplyAndChangesNoThreadStatus()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _,
                logger) =
            CreateDeps();

        var job = CreateJob();
        var authorizedIdentityId = Guid.NewGuid();
        var thread = new PrCommentThread(
            "77",
            "/src/Foo.cs",
            10,
            new List<PrThreadComment>
            {
                new("Bot", "Please fix this.", authorizedIdentityId),
                new("Dev", "Why?"),
            }.AsReadOnly());

        var pullRequest = CreatePullRequest([thread], authorizedIdentityId);
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
            .Returns(pullRequest);
        orchestrator.ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(new ReviewResult("Summary", new List<ReviewComment>().AsReadOnly()));

        var statusWriter = CreateThreadStatusWriter();
        var replyPublisher = Substitute.For<IReviewThreadReplyPublisher>();
        replyPublisher.Provider.Returns(Domain.Enums.ScmProvider.AzureDevOps);

        var service = CreateService(
            jobs,
            prFetcher,
            orchestrator,
            commentPoster,
            reviewerManager,
            clientRegistry,
            prScanRepository,
            logger,
            threadStatusWriter: statusWriter,
            threadReplyPublisher: replyPublisher);

        await service.ProcessAsync(job, CancellationToken.None);

        await replyPublisher.DidNotReceive().ReplyAsync(
            Arg.Any<Guid>(),
            Arg.Any<ReviewThreadRef>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await statusWriter.DidNotReceive().UpdateThreadStatusAsync(
            Arg.Any<Guid>(),
            Arg.Any<ReviewThreadRef>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await prScanRepository.DidNotReceive().SetLastSeenReplyCountsAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<IReadOnlyDictionary<string, int>>(),
            Arg.Any<CancellationToken>());
        await prScanRepository.DidNotReceive().SetLastSeenStatusesAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<IReadOnlyDictionary<string, string?>>(),
            Arg.Any<CancellationToken>());
        await prScanRepository.DidNotReceive().SetThreadPassWatermarkAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await prScanRepository.Received(1).SetReviewWatermarkAsync(
            job.ClientId,
            job.RepositoryId,
            job.PullRequestId,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }
}
