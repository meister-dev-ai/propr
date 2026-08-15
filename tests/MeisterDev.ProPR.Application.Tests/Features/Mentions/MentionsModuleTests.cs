// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Threading.Channels;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Interfaces;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Mentions;

public sealed class MentionsModuleTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid ConfigId = Guid.NewGuid();
    private static readonly Guid ReviewerId = Guid.NewGuid();

    private static readonly MentionConfigurationDto DefaultConfig = new(
        ConfigId,
        ClientId,
        ScmProvider.AzureDevOps,
        "https://dev.azure.com/org",
        "proj",
        60,
        true,
        DateTimeOffset.UtcNow,
        [
            new MentionRepoFilterDto(
                Guid.NewGuid(),
                "repo",
                // Claimed a week ago, as a stored configuration is. A filter with no claim time is treated
                // as claimed this instant, which answers nothing.
                ClaimedAt: DateTimeOffset.UtcNow.AddDays(-7)),
        ]);

    [Fact]
    public async Task ScanAsync_WhenUniqueMentionExists_StoresAndEnqueuesReplyJob()
    {
        var mentionConfigs = Substitute.For<IMentionConfigurationRepository>();
        var activePrFetcher = Substitute.For<IActivePrFetcher>();
        var pullRequestFetcher = Substitute.For<IPullRequestFetcher>();
        var scanRepository = Substitute.For<IMentionScanRepository>();
        var jobRepository = Substitute.For<IMentionReplyJobRepository>();
        var clientRegistry = Substitute.For<IClientRegistry>();
        var channel = Channel.CreateUnbounded<MentionReplyJob>();
        var sut = new MentionScanService(
            mentionConfigs,
            activePrFetcher,
            pullRequestFetcher,
            clientRegistry,
            scanRepository,
            jobRepository,
            channel.Writer,
            NullLogger<MentionScanService>.Instance);

        var pr = new ActivePullRequestRef("https://dev.azure.com/org", "proj", "repo", 1, DateTimeOffset.UtcNow);
        var pullRequest = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            1,
            "Test PR",
            null,
            "feature/test",
            "main",
            [],
            ExistingThreads:
            [
                new PrCommentThread(
                    "100",
                    null,
                    null,
                    [
                        new PrThreadComment(
                            "Alice",
                            $"@<{ReviewerId}> please help",
                            Guid.NewGuid(),
                            200,
                            DateTimeOffset.UtcNow),
                    ]),
            ]);

        mentionConfigs.GetAllActiveAsync(Arg.Any<CancellationToken>()).Returns([DefaultConfig]);
        activePrFetcher.GetRecentlyUpdatedPullRequestsAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns([pr]);
        clientRegistry.GetEffectiveReviewerIdentityAsync(ClientId, Arg.Any<ProviderHostRef>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewerIdentity(
                    new ProviderHostRef(DefaultConfig.Provider, DefaultConfig.ProviderScopePath),
                    ReviewerId.ToString("D"),
                    "review-bot",
                    "Review Bot",
                    false));
        pullRequestFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(pullRequest);
        jobRepository.ExistsForCommentAsync(
                "repo",
                1,
                "100",
                200,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(false);
        jobRepository.TryAddAsync(Arg.Any<MentionReplyJob>(), Arg.Any<CancellationToken>()).Returns(true);

        await sut.ScanAsync();

        await jobRepository.Received(1)
            .TryAddAsync(
                Arg.Is<MentionReplyJob>(job =>
                    job.ClientId == ClientId &&
                    job.PullRequestId == 1 &&
                    job.ThreadId == "100" &&
                    job.CommentId == 200),
                Arg.Any<CancellationToken>());
        Assert.Equal(1, channel.Reader.Count);
    }

    [Fact]
    public async Task ProcessAsync_WhenAnswerGenerated_PostsReplyAndCompletesJob()
    {
        var pullRequestFetcher = Substitute.For<IPullRequestFetcher>();
        var jobRepository = Substitute.For<IMentionReplyJobRepository>();
        var answerService = Substitute.For<IMentionAnswerService>();
        var threadReplier = Substitute.For<IReviewThreadReplyPublisher>();
        var providerRegistry = Substitute.For<IScmProviderRegistry>();
        threadReplier.Provider.Returns(ScmProvider.AzureDevOps);
        threadReplier.ReplyAsync(
                Arg.Any<Guid>(),
                Arg.Any<ReviewThreadRef>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        providerRegistry.GetReviewThreadReplyPublisher(Arg.Any<ScmProvider>())
            .Returns(threadReplier);
        var sut = new MentionReplyService(
            pullRequestFetcher,
            jobRepository,
            answerService,
            providerRegistry,
            NullLogger<MentionReplyService>.Instance);

        var job = new MentionReplyJob(
            Guid.NewGuid(),
            ClientId,
            "https://dev.azure.com/org",
            "proj",
            "repo",
            7,
            "3",
            11,
            "@bot please help");
        var pullRequest = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            7,
            1,
            "PR",
            null,
            "feature/a",
            "main",
            []);

        jobRepository.TryTransitionAsync(job.Id, MentionJobStatus.Pending, MentionJobStatus.Processing).Returns(true);
        pullRequestFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(pullRequest);
        answerService.AnswerAsync(pullRequest, ClientId, job.MentionText, job.ThreadId, Arg.Any<CancellationToken>())
            .Returns(new MentionAnswer("Here is the answer.", AiTokenUsage.Missing, "gpt-test"));

        await sut.ProcessAsync(job);

        await threadReplier.Received(1)
            .ReplyAsync(job.ClientId, job.ReviewThreadReference, "Here is the answer.", Arg.Any<CancellationToken>());
        await jobRepository.Received(1).SetCompletedAsync(job.Id, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }
}
