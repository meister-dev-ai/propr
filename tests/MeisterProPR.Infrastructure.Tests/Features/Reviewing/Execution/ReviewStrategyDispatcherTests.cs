// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.AgenticFileByFile;
using Microsoft.Extensions.AI;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution;

public sealed class ReviewStrategyDispatcherTests
{
    [Fact]
    public async Task ReviewAsync_AgenticFileByFile_RoutesToAgenticFileByFileOrchestrator()
    {
        var fileByFile = Substitute.For<IFileByFileReviewOrchestrator>();
        var agenticFileByFile = Substitute.For<IAgenticFileByFileReviewOrchestrator>();
        var prWideAgentic = Substitute.For<IPrWideAgenticReviewOrchestrator>();
        var expected = new ReviewResult("agentic-file", []);
        agenticFileByFile.ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(expected);
        var sut = new ReviewStrategyDispatcher(fileByFile, agenticFileByFile, prWideAgentic);
        var job = CreateJob(ReviewStrategy.AgenticFileByFile);
        var pr = CreatePr();
        var context = new ReviewSystemContext(null, [], null);

        var result = await sut.ReviewAsync(job, pr, context, CancellationToken.None);

        Assert.Same(expected, result);
        await agenticFileByFile.Received(1).ReviewAsync(job, pr, context, CancellationToken.None);
        await fileByFile.DidNotReceive().ReviewAsync(
            Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<IChatClient?>());
        await prWideAgentic.DidNotReceive().ReviewAsync(
            Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<IChatClient?>());
    }

    [Fact]
    public async Task ReviewAsync_PrWideAgentic_RoutesToPrWideOrchestrator()
    {
        var fileByFile = Substitute.For<IFileByFileReviewOrchestrator>();
        var agenticFileByFile = Substitute.For<IAgenticFileByFileReviewOrchestrator>();
        var prWideAgentic = Substitute.For<IPrWideAgenticReviewOrchestrator>();
        var expected = new ReviewResult("pr-wide", []);
        prWideAgentic.ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(expected);
        var sut = new ReviewStrategyDispatcher(fileByFile, agenticFileByFile, prWideAgentic);
        var job = CreateJob(ReviewStrategy.PrWideAgentic);
        var pr = CreatePr();
        var context = new ReviewSystemContext(null, [], null);

        var result = await sut.ReviewAsync(job, pr, context, CancellationToken.None);

        Assert.Same(expected, result);
        await prWideAgentic.Received(1).ReviewAsync(job, pr, context, CancellationToken.None);
        await fileByFile.DidNotReceive().ReviewAsync(
            Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<IChatClient?>());
        await agenticFileByFile.DidNotReceive().ReviewAsync(
            Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<IChatClient?>());
    }

    [Fact]
    public async Task ReviewAsync_FileByFile_RoutesToLegacyOrchestrator()
    {
        var fileByFile = Substitute.For<IFileByFileReviewOrchestrator>();
        var agenticFileByFile = Substitute.For<IAgenticFileByFileReviewOrchestrator>();
        var prWideAgentic = Substitute.For<IPrWideAgenticReviewOrchestrator>();
        var expected = new ReviewResult("file", []);
        fileByFile.ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(expected);
        var sut = new ReviewStrategyDispatcher(fileByFile, agenticFileByFile, prWideAgentic);
        var job = CreateJob(ReviewStrategy.FileByFile);
        var pr = CreatePr();
        var context = new ReviewSystemContext(null, [], null);

        var result = await sut.ReviewAsync(job, pr, context, CancellationToken.None);

        Assert.Same(expected, result);
        await fileByFile.Received(1).ReviewAsync(job, pr, context, CancellationToken.None);
        await agenticFileByFile.DidNotReceive().ReviewAsync(
            Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<IChatClient?>());
        await prWideAgentic.DidNotReceive().ReviewAsync(
            Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<IChatClient?>());
    }

    [Fact]
    public async Task ReviewAsync_WithExplicitPipelineProfile_UsesMatchingStrategyProfile()
    {
        var fileByFile = Substitute.For<IFileByFileReviewOrchestrator>();
        var agenticFileByFile = Substitute.For<IAgenticFileByFileReviewOrchestrator>();
        var prWideAgentic = Substitute.For<IPrWideAgenticReviewOrchestrator>();
        var profileProvider = Substitute.For<IReviewPipelineProfileProvider>();
        profileProvider.GetProfiles(ReviewStrategy.AgenticFileByFile).Returns(
        [
            new ReviewPipelineProfile(
                ReviewPipelineProfileProvider.AgenticExperimentalProfileId,
                "Agentic experimental",
                ReviewStrategy.AgenticFileByFile,
                [AgenticProRvPrefilterStage.StageIdConstant],
                [
                    AgenticSpeculativeCommentFilterStage.StageIdConstant,
                    AgenticInfoCommentStripStage.StageIdConstant,
                ],
                [ReviewPipelineProfileProvider.FinalizeStageFamilyId],
                false),
        ]);

        var expected = new ReviewResult("agentic-profile", []);
        agenticFileByFile.ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(expected);

        var sut = new ReviewStrategyDispatcher(fileByFile, agenticFileByFile, prWideAgentic, profileProvider);
        var job = CreateJob(ReviewStrategy.AgenticFileByFile);
        var pr = CreatePr();
        var context = new ReviewSystemContext(null, [], null);

        var result = await sut.ReviewAsync(
            job,
            pr,
            context,
            CancellationToken.None,
            pipelineProfileId: ReviewPipelineProfileProvider.AgenticExperimentalProfileId);

        Assert.Same(expected, result);
        profileProvider.Received(1).GetProfiles(ReviewStrategy.AgenticFileByFile);
        await agenticFileByFile.Received(1).ReviewAsync(job, pr, context, CancellationToken.None);
        await fileByFile.DidNotReceive().ReviewAsync(
            Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<IChatClient?>());
        await prWideAgentic.DidNotReceive().ReviewAsync(
            Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<IChatClient?>());
    }

    private static ReviewJob CreateJob(ReviewStrategy strategy)
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 42, 7);
        job.SelectReviewStrategy(
            strategy,
            ReviewStrategySelectionSource.ClientDefault,
            ReviewComparisonMode.Single,
            ReviewPublicationMode.Publish,
            null);
        return job;
    }

    private static PullRequest CreatePr()
    {
        return new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            42,
            7,
            "Test PR",
            null,
            "feature/x",
            "main",
            [new ChangedFile("src/Test.cs", ChangeType.Edit, "content", "diff")]);
    }
}
