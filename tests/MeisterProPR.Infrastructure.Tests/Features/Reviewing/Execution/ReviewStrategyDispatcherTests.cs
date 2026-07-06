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
    public async Task ReviewAsync_AgenticFileByFile_ThrowsDisabledStrategyError()
    {
        var fileByFile = Substitute.For<IFileByFileReviewOrchestrator>();
        var sut = new ReviewStrategyDispatcher(fileByFile);
        var job = CreateJob(ReviewStrategy.AgenticFileByFile);
        var pr = CreatePr();
        var context = new ReviewSystemContext(null, [], null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.ReviewAsync(job, pr, context, CancellationToken.None));

        Assert.Contains("currently disabled", ex.Message, StringComparison.OrdinalIgnoreCase);
        await fileByFile.DidNotReceive().ReviewAsync(
            Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<IChatClient?>());
    }

    [Fact]
    public async Task ReviewAsync_PrWideAgentic_ThrowsDisabledStrategyError()
    {
        var fileByFile = Substitute.For<IFileByFileReviewOrchestrator>();
        var sut = new ReviewStrategyDispatcher(fileByFile);
        var job = CreateJob(ReviewStrategy.PrWideAgentic);
        var pr = CreatePr();
        var context = new ReviewSystemContext(null, [], null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.ReviewAsync(job, pr, context, CancellationToken.None));

        Assert.Contains("currently disabled", ex.Message, StringComparison.OrdinalIgnoreCase);
        await fileByFile.DidNotReceive().ReviewAsync(
            Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<IChatClient?>());
    }

    [Fact]
    public async Task ReviewAsync_FileByFile_RoutesToLegacyOrchestrator()
    {
        var fileByFile = Substitute.For<IFileByFileReviewOrchestrator>();
        var expected = new ReviewResult("file", []);
        fileByFile.ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(expected);
        var sut = new ReviewStrategyDispatcher(fileByFile);
        var job = CreateJob(ReviewStrategy.FileByFile);
        var pr = CreatePr();
        var context = new ReviewSystemContext(null, [], null);

        var result = await sut.ReviewAsync(job, pr, context, CancellationToken.None);

        Assert.Same(expected, result);
        await fileByFile.Received(1).ReviewAsync(job, pr, context, CancellationToken.None);
    }

    [Fact]
    public async Task ReviewAsync_WithDisabledStrategyAndExplicitPipelineProfile_ThrowsDisabledStrategyError()
    {
        var fileByFile = Substitute.For<IFileByFileReviewOrchestrator>();
        var profileProvider = Substitute.For<IReviewPipelineProfileProvider>();
        profileProvider.GetProfiles(ReviewStrategy.AgenticFileByFile).Returns(
        [
            new ReviewPipelineProfile(
                ReviewPipelineProfileProvider.AgenticExperimentalProfileId,
                "Agentic experimental",
                ReviewStrategy.AgenticFileByFile,
                [AgenticProRvPrefilterStage.StageIdConstant],
                [
                    AgenticInfoCommentStripStage.StageIdConstant,
                ],
                [ReviewPipelineProfileProvider.FinalizeStageFamilyId],
                false),
        ]);

        var sut = new ReviewStrategyDispatcher(fileByFile, profileProvider);
        var job = CreateJob(ReviewStrategy.AgenticFileByFile);
        var pr = CreatePr();
        var context = new ReviewSystemContext(null, [], null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.ReviewAsync(
                job,
                pr,
                context,
                CancellationToken.None,
                pipelineProfileId: ReviewPipelineProfileProvider.AgenticExperimentalProfileId));

        Assert.Contains("currently disabled", ex.Message, StringComparison.OrdinalIgnoreCase);
        profileProvider.DidNotReceive().GetProfiles(ReviewStrategy.AgenticFileByFile);
        await fileByFile.DidNotReceive().ReviewAsync(
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
