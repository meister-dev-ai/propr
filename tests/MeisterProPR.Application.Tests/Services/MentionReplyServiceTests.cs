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
    private readonly MentionReplyService _sut;
    private readonly IAdoThreadReplier _threadReplier = Substitute.For<IAdoThreadReplier>();

    public MentionReplyServiceTests()
    {
        this._sut = new MentionReplyService(
            this._prFetcher,
            this._jobRepository,
            this._answerService,
            this._threadReplier,
            NullLogger<MentionReplyService>.Instance);
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
        return new PullRequest(orgUrl, projectId, repoId, prId, 1, "PR Title", null, "feat/x", "main", []);
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
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(pr);
        this._answerService.AnswerAsync(Arg.Any<PullRequest>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(answer);

        // Act
        await this._sut.ProcessAsync(job);

        // Assert: reply was posted and job marked completed
        await this._threadReplier.Received(1)
            .ReplyAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.ThreadId,
                answer,
                job.ClientId,
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
        await this._prFetcher.DidNotReceiveWithAnyArgs().FetchAsync(default!, default!, default!, default, default);
        await this._threadReplier.DidNotReceiveWithAnyArgs().ReplyAsync(default!, default!, default!, default, default, default!);
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
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(pr);
        this._answerService.AnswerAsync(Arg.Any<PullRequest>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs<InvalidOperationException>();

        // Act
        await this._sut.ProcessAsync(job);

        // Assert: job marked failed, no reply posted
        await this._jobRepository.Received(1)
            .SetFailedAsync(
                job.Id,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
        await this._threadReplier.DidNotReceiveWithAnyArgs().ReplyAsync(default!, default!, default!, default, default, default!);
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
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(pr);
        this._answerService.AnswerAsync(Arg.Any<PullRequest>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(answer);
        this._threadReplier.ReplyAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<Guid?>(),
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
}
