// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Threading.Channels;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.Interfaces;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Features.Mentions;

public sealed class MentionsModuleTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid ConfigId = Guid.NewGuid();
    private static readonly Guid ReviewerId = Guid.NewGuid();

    private static readonly CrawlConfigurationDto DefaultConfig = new(
        ConfigId,
        ClientId,
        "https://dev.azure.com/org",
        "proj",
        ReviewerId,
        60,
        true,
        DateTimeOffset.UtcNow,
        []);

    [Fact]
    public async Task ScanAsync_WhenUniqueMentionExists_StoresAndEnqueuesReplyJob()
    {
        var crawlConfigs = Substitute.For<ICrawlConfigurationRepository>();
        var activePrFetcher = Substitute.For<IActivePrFetcher>();
        var pullRequestFetcher = Substitute.For<IPullRequestFetcher>();
        var scanRepository = Substitute.For<IMentionScanRepository>();
        var jobRepository = Substitute.For<IMentionReplyJobRepository>();
        var channel = Channel.CreateUnbounded<MentionReplyJob>();
        var sut = new MentionScanService(
            crawlConfigs,
            activePrFetcher,
            pullRequestFetcher,
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
                    100,
                    null,
                    null,
                    [new PrThreadComment("Alice", $"@<{ReviewerId}> please help", Guid.NewGuid(), 200, DateTimeOffset.UtcNow)]),
            ]);

        crawlConfigs.GetAllActiveAsync(Arg.Any<CancellationToken>()).Returns([DefaultConfig]);
        activePrFetcher.GetRecentlyUpdatedPullRequestsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns([pr]);
        pullRequestFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(pullRequest);
        jobRepository.ExistsForCommentAsync(ClientId, 1, 100, 200, Arg.Any<CancellationToken>()).Returns(false);

        await sut.ScanAsync();

        await jobRepository.Received(1).AddAsync(
            Arg.Is<MentionReplyJob>(job =>
                job.ClientId == ClientId &&
                job.PullRequestId == 1 &&
                job.ThreadId == 100 &&
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
        var threadReplier = Substitute.For<IAdoThreadReplier>();
        var sut = new MentionReplyService(
            pullRequestFetcher,
            jobRepository,
            answerService,
            threadReplier,
            NullLogger<MentionReplyService>.Instance);

        var job = new MentionReplyJob(Guid.NewGuid(), ClientId, "https://dev.azure.com/org", "proj", "repo", 7, 3, 11, "@bot please help");
        var pullRequest = new PullRequest("https://dev.azure.com/org", "proj", "repo", "repo", 7, 1, "PR", null, "feature/a", "main", []);

        jobRepository.TryTransitionAsync(job.Id, MentionJobStatus.Pending, MentionJobStatus.Processing).Returns(true);
        pullRequestFetcher.FetchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(pullRequest);
        answerService.AnswerAsync(pullRequest, ClientId, job.MentionText, job.ThreadId, Arg.Any<CancellationToken>())
            .Returns("Here is the answer.");

        await sut.ProcessAsync(job);

        await threadReplier.Received(1).ReplyAsync(job.OrganizationUrl, job.ProjectId, job.RepositoryId, job.PullRequestId, job.ThreadId, "Here is the answer.", job.ClientId, Arg.Any<CancellationToken>());
        await jobRepository.Received(1).SetCompletedAsync(job.Id, Arg.Any<CancellationToken>());
    }
}
