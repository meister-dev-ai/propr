// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.Interfaces;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterProPR.Application.Tests.Services;

/// <summary>Unit tests for <see cref="MentionReplyService" />.</summary>
public sealed class MentionReplyServiceTests
{
    private static readonly Guid ClientId = Guid.NewGuid();

    private readonly IMentionAnswerService _answerService =
        Substitute.For<IMentionAnswerService>();

    private readonly IMentionReplyJobRepository _jobRepository = Substitute.For<IMentionReplyJobRepository>();

    private readonly IPullRequestFetcher _prFetcher = Substitute.For<IPullRequestFetcher>();

    private readonly IProviderActivationService _providerActivationService =
        Substitute.For<IProviderActivationService>();

    private readonly IScmProviderRegistry _providerRegistry = Substitute.For<IScmProviderRegistry>();
    private readonly MentionReplyService _sut;
    private readonly IReviewThreadReplyPublisher _threadReplier = Substitute.For<IReviewThreadReplyPublisher>();

    public MentionReplyServiceTests()
    {
        this._threadReplier.Provider.Returns(ScmProvider.AzureDevOps);
        this._threadReplier.ReplyAsync(
                Arg.Any<Guid>(),
                Arg.Any<ReviewThreadRef>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        this._providerRegistry.GetReviewThreadReplyPublisher(Arg.Any<ScmProvider>())
            .Returns(this._threadReplier);

        this._sut = new MentionReplyService(
            this._prFetcher,
            this._jobRepository,
            this._answerService,
            this._providerRegistry,
            NullLogger<MentionReplyService>.Instance,
            this._providerActivationService);

        this._providerActivationService.IsEnabledAsync(Arg.Any<ScmProvider>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    private static MentionReplyJob MakeJob(
        Guid? clientId = null,
        string orgUrl = "https://dev.azure.com/org",
        string projectId = "proj",
        string repoId = "repo",
        int prId = 1,
        int threadId = 10,
        int commentId = 100,
        string mentionText = "what does this method do?")
    {
        return new MentionReplyJob(
            Guid.NewGuid(),
            clientId ?? ClientId,
            orgUrl,
            projectId,
            repoId,
            prId,
            threadId,
            commentId,
            mentionText);
    }

    private static PullRequest MakePullRequest(
        string orgUrl = "https://dev.azure.com/org",
        string projectId = "proj",
        string repoId = "repo",
        int prId = 1)
    {
        return new PullRequest(orgUrl, projectId, repoId, repoId, prId, 1, "PR Title", null, "feat/x", "main", []);
    }

    [Fact]
    public async Task ProcessAsync_HappyPath_TransitionsToCompletedAndReplies()
    {
        // Arrange
        var job = MakeJob();
        var pr = MakePullRequest();
        var answer = "The method calculates the sum.";

        this._jobRepository.TryTransitionAsync(job.Id, MentionJobStatus.Pending, MentionJobStatus.Processing)
            .Returns(true);
        this._prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(pr);
        this._answerService.AnswerAsync(
                Arg.Any<PullRequest>(),
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(answer);

        // Act
        await this._sut.ProcessAsync(job);

        // Assert: reply was posted and job marked completed
        await this._threadReplier.Received(1)
            .ReplyAsync(
                job.ClientId,
                job.ReviewThreadReference,
                answer,
                Arg.Any<CancellationToken>());
        await this._jobRepository.Received(1).SetCompletedAsync(job.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_JobAlreadyProcessing_SkipsProcessing()
    {
        // Arrange: transition from Pending → Processing fails (job taken by another worker)
        var job = MakeJob();
        this._jobRepository.TryTransitionAsync(job.Id, MentionJobStatus.Pending, MentionJobStatus.Processing)
            .Returns(false);

        // Act
        await this._sut.ProcessAsync(job);

        // Assert: no PR fetch, no reply, no state change
        await this._prFetcher.DidNotReceiveWithAnyArgs().FetchAsync(null!, null!, null!, 0, 0);
        await this._threadReplier.DidNotReceiveWithAnyArgs().ReplyAsync(default, default!, default!);
    }

    [Fact]
    public async Task ProcessAsync_AiServiceThrows_MarksJobFailed()
    {
        // Arrange
        var job = MakeJob();
        var pr = MakePullRequest();

        this._jobRepository.TryTransitionAsync(job.Id, MentionJobStatus.Pending, MentionJobStatus.Processing)
            .Returns(true);
        this._prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(pr);
        this._answerService.AnswerAsync(
                Arg.Any<PullRequest>(),
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs<InvalidOperationException>();

        // Act
        await this._sut.ProcessAsync(job);

        // Assert: job marked failed, no reply posted
        await this._jobRepository.Received(1)
            .SetFailedAsync(
                job.Id,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
        await this._threadReplier.DidNotReceiveWithAnyArgs().ReplyAsync(default, default!, default!);
    }

    [Fact]
    public async Task ProcessAsync_ThreadReplierThrows_MarksJobFailed()
    {
        // Arrange
        var job = MakeJob();
        var pr = MakePullRequest();
        var answer = "An answer.";

        this._jobRepository.TryTransitionAsync(job.Id, MentionJobStatus.Pending, MentionJobStatus.Processing)
            .Returns(true);
        this._prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(pr);
        this._answerService.AnswerAsync(
                Arg.Any<PullRequest>(),
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(answer);
        this._threadReplier.ReplyAsync(
                Arg.Any<Guid>(),
                Arg.Any<ReviewThreadRef>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs<HttpRequestException>();

        // Act
        await this._sut.ProcessAsync(job);

        // Assert: job marked failed
        await this._jobRepository.Received(1)
            .SetFailedAsync(
                job.Id,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_DisabledProvider_MarksJobFailedWithoutFetchingPullRequest()
    {
        var job = MakeJob();

        this._jobRepository.TryTransitionAsync(job.Id, MentionJobStatus.Pending, MentionJobStatus.Processing)
            .Returns(true);
        this._providerActivationService.IsEnabledAsync(job.Provider, Arg.Any<CancellationToken>())
            .Returns(false);

        await this._sut.ProcessAsync(job);

        await this._prFetcher.DidNotReceiveWithAnyArgs().FetchAsync(null!, null!, null!, 0, 0);
        await this._threadReplier.DidNotReceiveWithAnyArgs().ReplyAsync(default, default!, default!);
        await this._jobRepository.Received(1)
            .SetFailedAsync(
                job.Id,
                Arg.Is<string>(message => message.Contains("disabled", StringComparison.OrdinalIgnoreCase)),
                Arg.Any<CancellationToken>());
    }
}
