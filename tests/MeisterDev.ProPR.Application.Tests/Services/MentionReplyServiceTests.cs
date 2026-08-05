// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.Interfaces;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Application.Tests.Services;

/// <summary>Unit tests for <see cref="MentionReplyService" />.</summary>
public sealed class MentionReplyServiceTests
{
    private static readonly Guid ClientId = Guid.NewGuid();

    private readonly IMentionAnswerService _answerService =
        Substitute.For<IMentionAnswerService>();

    private readonly IMentionReplyJobRepository _jobRepository = Substitute.For<IMentionReplyJobRepository>();

    private readonly IPostedCommentOriginStore _originStore = Substitute.For<IPostedCommentOriginStore>();

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
            .Returns(Task.FromResult<string?>(null));
        this._providerRegistry.GetReviewThreadReplyPublisher(Arg.Any<ScmProvider>())
            .Returns(this._threadReplier);

        this._sut = new MentionReplyService(
            this._prFetcher,
            this._jobRepository,
            this._answerService,
            this._providerRegistry,
            NullLogger<MentionReplyService>.Instance,
            this._providerActivationService,
            this._originStore);

        this._providerActivationService.IsEnabledAsync(Arg.Any<ScmProvider>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    private static MentionReplyJob MakeJob(
        Guid? clientId = null,
        string orgUrl = "https://dev.azure.com/org",
        string projectId = "proj",
        string repoId = "repo",
        int prId = 1,
        string threadId = "10",
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

    private void SetupAnsweredMention(MentionReplyJob job, string answer)
    {
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
            .ReturnsForAnyArgs(MakePullRequest());
        this._answerService.AnswerAsync(
                Arg.Any<PullRequest>(),
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .ReturnsForAnyArgs(answer);
    }

    private void SetupPostedCommentId(string? postedCommentId)
    {
        this._threadReplier.ReplyAsync(
                Arg.Any<Guid>(),
                Arg.Any<ReviewThreadRef>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(postedCommentId));
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
                Arg.Any<string>(),
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
        await this._jobRepository.Received(1).SetCompletedAsync(job.Id, Arg.Any<string?>(), Arg.Any<CancellationToken>());
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
                Arg.Any<string>(),
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
                Arg.Any<string>(),
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
    public async Task ProcessAsync_HappyPath_RecordsTheAnswerAgainstTheMentionJob()
    {
        // The mention answer is a comment ProPR authored, and the mention job is what posted it, so its own
        // id is the provenance the row carries.
        var job = MakeJob();
        SetupAnsweredMention(job, "The method calculates the sum.");
        this.SetupPostedCommentId("answer-comment-5");

        await this._sut.ProcessAsync(job);

        await this._originStore.Received(1).RecordAsync(
            Arg.Is<IReadOnlyList<PostedCommentOriginEntry>>(entries =>
                entries.Count == 1
                && entries[0].ClientId == job.ClientId
                && entries[0].RepositoryId == job.RepositoryId
                && entries[0].PullRequestId == job.PullRequestId
                && entries[0].ProviderThreadId == "10"
                && entries[0].ProviderCommentId == "answer-comment-5"
                && entries[0].JobId == job.Id),
            Arg.Any<CancellationToken>());
        await this._jobRepository.Received(1).SetCompletedAsync(job.Id, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_HappyPath_CompletesTheJobWithTheCommentIdItPosted()
    {
        // The completion update is the only write guaranteed to happen after the answer is posted, so it is
        // where the comment id has to land. Without it on the job, a crash before the provenance row is written
        // loses the attribution for good: the answer stays on the pull request and nothing can say who posted it.
        var job = MakeJob();
        SetupAnsweredMention(job, "An answer.");
        this.SetupPostedCommentId("answer-comment-5");

        await this._sut.ProcessAsync(job);

        await this._jobRepository.Received(1)
            .SetCompletedAsync(job.Id, "answer-comment-5", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_ReplyPublisherReportsNoCommentId_CompletesWithoutRecording()
    {
        var job = MakeJob();
        SetupAnsweredMention(job, "An answer.");
        this.SetupPostedCommentId(null);

        await this._sut.ProcessAsync(job);

        await this._originStore.DidNotReceive().RecordAsync(
            Arg.Any<IReadOnlyList<PostedCommentOriginEntry>>(),
            Arg.Any<CancellationToken>());
        await this._jobRepository.Received(1).SetCompletedAsync(job.Id, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_ProvenanceRecordingFails_StillCompletesTheJob()
    {
        // The answer is already on the pull request by then. A bookkeeping failure must not report it as a
        // reply that never happened.
        var job = MakeJob();
        SetupAnsweredMention(job, "An answer.");
        this.SetupPostedCommentId("answer-comment-5");
        this._originStore.RecordAsync(Arg.Any<IReadOnlyList<PostedCommentOriginEntry>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("origin store unavailable"));

        await this._sut.ProcessAsync(job);

        await this._jobRepository.Received(1).SetCompletedAsync(job.Id, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await this._jobRepository.DidNotReceiveWithAnyArgs().SetFailedAsync(default, default!);
    }

    [Fact]
    public async Task ProcessAsync_CancelledWhileRecordingTheAnswer_StillCompletesTheJob()
    {
        // The recorder rethrows on cancellation rather than claiming a row it never wrote, and the outer
        // handler lets a cancellation through. So the recording has to run after the job is completed: in
        // front of it, a cancellation would leave the answer posted and the job stuck in Processing, which the
        // next startup resets to Pending and works again, posting the same answer a second time.
        using var cancellation = new CancellationTokenSource();
        var job = MakeJob();
        SetupAnsweredMention(job, "An answer.");

        // The claim runs with the run's own token rather than the default one the shared setup matches on.
        this._jobRepository.TryTransitionAsync(
                job.Id,
                MentionJobStatus.Pending,
                MentionJobStatus.Processing,
                Arg.Any<CancellationToken>())
            .Returns(true);
        this._threadReplier.ReplyAsync(
                Arg.Any<Guid>(),
                Arg.Any<ReviewThreadRef>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                // The run is cancelled the instant the answer lands on the pull request.
                cancellation.Cancel();
                return Task.FromResult<string?>("answer-comment-5");
            });
        this._originStore.RecordAsync(Arg.Any<IReadOnlyList<PostedCommentOriginEntry>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => this._sut.ProcessAsync(job, cancellation.Token));

        await this._jobRepository.Received(1).SetCompletedAsync(job.Id, Arg.Any<string?>(), Arg.Any<CancellationToken>());
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
