// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Budgeting.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;
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
    private const string ModelId = "gpt-test";
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid ConnectionId = Guid.NewGuid();

    private readonly IMentionAnswerService _answerService =
        Substitute.For<IMentionAnswerService>();

    private readonly IBudgetCapsProvider _capsProvider = Substitute.For<IBudgetCapsProvider>();

    private readonly IBudgetEventPublisher _budgetEventPublisher = Substitute.For<IBudgetEventPublisher>();

    private readonly IPullRequestIterationResolver _iterationResolver =
        Substitute.For<IPullRequestIterationResolver>();

    private readonly IMentionReplyJobRepository _jobRepository = Substitute.For<IMentionReplyJobRepository>();

    private readonly IPostedCommentOriginStore _originStore = Substitute.For<IPostedCommentOriginStore>();

    private readonly IPullRequestFetcher _prFetcher = Substitute.For<IPullRequestFetcher>();

    private readonly IProtocolRecorder _protocolRecorder = Substitute.For<IProtocolRecorder>();

    private readonly IProviderActivationService _providerActivationService =
        Substitute.For<IProviderActivationService>();

    private readonly IScmProviderRegistry _providerRegistry = Substitute.For<IScmProviderRegistry>();

    private readonly BudgetScopeAccessor _scopeAccessor = new();

    private readonly IReviewSpendAccumulator _spendAccumulator = Substitute.For<IReviewSpendAccumulator>();

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

        this._iterationResolver.GetLatestIterationIdAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(7);

        // Uncapped unless a test says otherwise, so the existing behaviour is what the default exercises.
        this._capsProvider.GetCapsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(BudgetCaps.None);

        this._sut = this.CreateService();

        this._providerActivationService.IsEnabledAsync(Arg.Any<ScmProvider>(), Arg.Any<CancellationToken>())
            .Returns(true);
    }

    private MentionReplyService CreateService()
    {
        return new MentionReplyService(
            this._prFetcher,
            this._jobRepository,
            this._answerService,
            this._providerRegistry,
            NullLogger<MentionReplyService>.Instance,
            this._providerActivationService,
            this._originStore,
            this._protocolRecorder,
            this._iterationResolver,
            this._capsProvider,
            this._spendAccumulator,
            this._scopeAccessor,
            this._budgetEventPublisher);
    }

    /// <summary>Puts the client at <paramref name="spentUsd" /> against a hard monthly cap of 10 USD.</summary>
    private void SetupSpendAgainstMonthlyHardCap(decimal spentUsd)
    {
        this.SetupSpendAgainstMonthlyCaps(spentUsd, softCapUsd: null, hardCapUsd: 10m);
    }

    /// <summary>Puts the client at <paramref name="spentUsd" /> against the given monthly caps.</summary>
    private void SetupSpendAgainstMonthlyCaps(decimal spentUsd, decimal? softCapUsd, decimal? hardCapUsd)
    {
        this._capsProvider.GetCapsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new BudgetCaps(softCapUsd, hardCapUsd, null, null, null, null));
        this._spendAccumulator.GetBaselineAsync(
                Arg.Any<ReviewSpendSubject>(),
                Arg.Any<DateOnly>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new ReviewSpendBaseline(
                    new ReviewScopeSpend(spentUsd, false),
                    new ReviewScopeSpend(0m, false),
                    new ReviewScopeSpend(0m, false)));
    }

    private static MentionAnswer MakeAnswer(
        string text = "An answer.",
        long inputTokens = 1_200,
        long outputTokens = 300)
    {
        return new MentionAnswer(
            text,
            new AiTokenUsage(inputTokens, outputTokens),
            ModelId,
            ConnectionId,
            "reviewer-default");
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
        this.SetupAnsweredMention(job, MakeAnswer(answer));
    }

    private void SetupAnsweredMention(MentionReplyJob job, MentionAnswer answer)
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
        var answer = "The method calculates the sum.";
        SetupAnsweredMention(job, answer);

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
        SetupAnsweredMention(job, "An answer.");
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

    [Fact]
    public async Task ProcessAsync_HappyPath_RecordsWhatTheAnswerSpent()
    {
        // Closing the trace record is what moves the tokens onto the job row and the client's usage, so an
        // answer that never opens one spends money nothing can see.
        var job = MakeJob();
        SetupAnsweredMention(job, MakeAnswer(inputTokens: 1_200, outputTokens: 300));
        this._protocolRecorder.BeginForMentionReplyAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<string>())
            .Returns(Guid.NewGuid());

        await this._sut.ProcessAsync(job);

        await this._protocolRecorder.Received(1).BeginForMentionReplyAsync(
            job.Id,
            Arg.Any<string>(),
            ModelId,
            Arg.Any<CancellationToken>(),
            Arg.Any<string>());
        await this._protocolRecorder.Received(1).SetCompletedAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            1_200,
            300,
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<int?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<long?>(),
            Arg.Any<CacheObservabilityStatus>(),
            Arg.Any<long?>(),
            Arg.Any<long?>());
    }

    [Fact]
    public async Task ProcessAsync_HappyPath_StoresTheIncrementAndRuntimeTheSpendIsPricedAgainst()
    {
        var job = MakeJob();
        SetupAnsweredMention(job, MakeAnswer());

        await this._sut.ProcessAsync(job);

        await this._jobRepository.Received(1).SetExecutionContextAsync(
            job.Id,
            7,
            ConnectionId,
            ModelId,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_LatestRevisionUnreadable_StillAnswersAndChargesTheWholePullRequest()
    {
        // A null increment widens the increment scope to the pull request rather than dropping the row out of
        // it, so a provider hiccup cannot become a way past an increment cap.
        var job = MakeJob();
        SetupAnsweredMention(job, MakeAnswer());
        this._iterationResolver.GetLatestIterationIdAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs<HttpRequestException>();

        await this._sut.ProcessAsync(job);

        await this._jobRepository.Received(1).SetExecutionContextAsync(
            job.Id,
            null,
            Arg.Any<Guid?>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await this._jobRepository.Received(1)
            .SetCompletedAsync(job.Id, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_HardCapAlreadyReached_MakesNoModelCallAndSaysWhy()
    {
        // A person is waiting on an answer. Saying nothing reads as the reviewer ignoring them, and they
        // cannot lift the cap themselves.
        var job = MakeJob();
        SetupAnsweredMention(job, MakeAnswer());
        this.SetupSpendAgainstMonthlyHardCap(11m);

        await this.CreateService().ProcessAsync(job);

        await this._answerService.DidNotReceiveWithAnyArgs()
            .AnswerAsync(default!, default, default!, default!);
        await this._threadReplier.Received(1).ReplyAsync(
            job.ClientId,
            job.ReviewThreadReference,
            Arg.Is<string>(text => text.Contains("budget", StringComparison.OrdinalIgnoreCase)),
            Arg.Any<CancellationToken>());
        await this._jobRepository.Received(1).SetBudgetHeldAsync(
            job.Id,
            Arg.Any<int?>(),
            BudgetScopeKind.ClientMonthly,
            BudgetCapKind.Hard,
            10m,
            11m,
            Arg.Any<CancellationToken>());
        await this._jobRepository.DidNotReceiveWithAnyArgs().SetCompletedAsync(default, default);
    }

    [Fact]
    public async Task ProcessAsync_HardCapAlreadyReached_PublishesTheBudgetEvent()
    {
        var job = MakeJob();
        SetupAnsweredMention(job, MakeAnswer());
        this.SetupSpendAgainstMonthlyHardCap(11m);

        await this.CreateService().ProcessAsync(job);

        await this._budgetEventPublisher.Received(1).PublishAsync(
            Arg.Is<BudgetEventNotification>(notification =>
                notification.ClientId == job.ClientId
                && notification.EventType == BudgetEventType.HardCapReached
                && notification.Scope == BudgetScopeKind.ClientMonthly),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_CapReachedByTheAnswersOwnCall_EndsHeldAndSaysWhy()
    {
        // The enforcing chat client throws once the running total crosses a hard cap. The developer is owed
        // the same explanation they would have had if the cap had been reached a moment earlier.
        var job = MakeJob();
        SetupAnsweredMention(job, MakeAnswer());
        this.SetupSpendAgainstMonthlyHardCap(0m);
        this._answerService.AnswerAsync(
                Arg.Any<PullRequest>(),
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new BudgetHardCapReachedException(new BudgetBreach(BudgetScopeKind.PullRequest, BudgetCapKind.Hard, 4m, 4.5m)));

        await this.CreateService().ProcessAsync(job);

        await this._jobRepository.Received(1).SetBudgetHeldAsync(
            job.Id,
            Arg.Any<int?>(),
            BudgetScopeKind.PullRequest,
            BudgetCapKind.Hard,
            4m,
            4.5m,
            Arg.Any<CancellationToken>());
        await this._jobRepository.DidNotReceiveWithAnyArgs().SetFailedAsync(default, default!);
    }

    [Fact]
    public async Task ProcessAsync_BudgetBlockCannotBeRecorded_SaysNothingSoTheRetryDoesNotRepeatIt()
    {
        // A job left in Processing returns to Pending at the next startup and runs again. A note posted
        // before the status was recorded would be posted a second time on every restart.
        var job = MakeJob();
        SetupAnsweredMention(job, MakeAnswer());
        this.SetupSpendAgainstMonthlyHardCap(11m);
        this._jobRepository.SetBudgetHeldAsync(
                Arg.Any<Guid>(),
                Arg.Any<int?>(),
                Arg.Any<BudgetScopeKind>(),
                Arg.Any<BudgetCapKind>(),
                Arg.Any<decimal>(),
                Arg.Any<decimal>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("the database went away"));

        await this.CreateService().ProcessAsync(job);

        await this._threadReplier.DidNotReceiveWithAnyArgs().ReplyAsync(default, default!, default!);
        await this._jobRepository.DidNotReceiveWithAnyArgs().SetFailedAsync(default, default!);
    }

    [Fact]
    public async Task ProcessAsync_BudgetEventPublishThrows_LeavesTheJobRecordedAsHeld()
    {
        // The publish runs after the status write. An exception escaping it would be caught by the outer
        // handler and overwrite the block that was just recorded with a generic failure.
        var job = MakeJob();
        SetupAnsweredMention(job, MakeAnswer());
        this.SetupSpendAgainstMonthlyHardCap(11m);
        this._budgetEventPublisher.PublishAsync(
                Arg.Any<BudgetEventNotification>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("publisher unavailable"));

        await this.CreateService().ProcessAsync(job);

        await this._jobRepository.Received(1).SetBudgetHeldAsync(
            job.Id,
            Arg.Any<int?>(),
            BudgetScopeKind.ClientMonthly,
            BudgetCapKind.Hard,
            10m,
            11m,
            Arg.Any<CancellationToken>());
        await this._jobRepository.DidNotReceiveWithAnyArgs().SetFailedAsync(default, default!);
    }

    [Fact]
    public async Task ProcessAsync_SoftCapReached_AnswersAnyway()
    {
        // A soft cap gates new work. Someone already in the conversation is waiting, and refusing here would
        // be permanent, so one crossed warning threshold would silence every mention on the installation.
        var job = MakeJob();
        SetupAnsweredMention(job, MakeAnswer());
        this.SetupSpendAgainstMonthlyCaps(9m, softCapUsd: 8m, hardCapUsd: 20m);

        await this.CreateService().ProcessAsync(job);

        await this._threadReplier.Received(1).ReplyAsync(
            job.ClientId,
            job.ReviewThreadReference,
            "An answer.",
            Arg.Any<CancellationToken>());
        await this._jobRepository.Received(1)
            .SetCompletedAsync(job.Id, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await this._jobRepository.DidNotReceiveWithAnyArgs().SetBudgetHeldAsync(default, default, default, default, default, default);
    }

    [Fact]
    public async Task ProcessAsync_SoftCapReached_PublishesTheSoftCapEventAnyway()
    {
        // The threshold still has to be visible, or a client crosses it entirely through mention spend and
        // nobody is told.
        var job = MakeJob();
        SetupAnsweredMention(job, MakeAnswer());
        this.SetupSpendAgainstMonthlyCaps(9m, softCapUsd: 8m, hardCapUsd: 20m);

        await this.CreateService().ProcessAsync(job);

        await this._budgetEventPublisher.Received(1).PublishAsync(
            Arg.Is<BudgetEventNotification>(notification =>
                notification.EventType == BudgetEventType.SoftCapReached
                && notification.Scope == BudgetScopeKind.ClientMonthly
                && notification.ThresholdUsd == 8m),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_SoftCapEventPublishThrows_StillPostsTheAnswer()
    {
        var job = MakeJob();
        SetupAnsweredMention(job, MakeAnswer());
        this.SetupSpendAgainstMonthlyCaps(9m, softCapUsd: 8m, hardCapUsd: 20m);
        this._budgetEventPublisher.PublishAsync(
                Arg.Any<BudgetEventNotification>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("publisher unavailable"));

        await this.CreateService().ProcessAsync(job);

        await this._jobRepository.Received(1)
            .SetCompletedAsync(job.Id, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await this._jobRepository.DidNotReceiveWithAnyArgs().SetFailedAsync(default, default!);
    }

    [Fact]
    public async Task ProcessAsync_HardCapAlreadyReached_RecordsTheIncrementItResolved()
    {
        // The refused path returns before the write that would otherwise carry the increment, so the block
        // has to record it or the row and the budget event disagree about the same fact.
        var job = MakeJob();
        SetupAnsweredMention(job, MakeAnswer());
        this.SetupSpendAgainstMonthlyHardCap(11m);

        await this.CreateService().ProcessAsync(job);

        await this._jobRepository.Received(1).SetBudgetHeldAsync(
            job.Id,
            7,
            Arg.Any<BudgetScopeKind>(),
            Arg.Any<BudgetCapKind>(),
            Arg.Any<decimal>(),
            Arg.Any<decimal>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_BudgetCutArrivesWrapped_IsStillTreatedAsACut()
    {
        // The enforcing chat client records the breach on the scope before throwing. An intervening layer that
        // wraps the exception must not turn a reached cap into a generic failure.
        //
        // The scope is tripped by hand here because no production wiring can trip it for one model call: the
        // enforcing client checks the cap before the call and prices the response after it, so a single call
        // that admission already cleared cannot throw. This guards the handler for the day the answer grows a
        // second call or a tool loop; it is not evidence that the path runs today.
        var job = MakeJob();
        SetupAnsweredMention(job, MakeAnswer());
        this.SetupSpendAgainstMonthlyHardCap(0m);
        this._answerService.AnswerAsync(
                Arg.Any<PullRequest>(),
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(callInfo =>
            {
                // Trip the ambient scope the way a real model call would, then hide the cause behind a wrapper.
                var scope = this._scopeAccessor.Current;
                Assert.NotNull(scope);
                scope.RecordCall(50m);
                var tripped = Record.Exception(() => scope.ThrowIfHardCapReached());
                return new InvalidOperationException("the provider call failed", tripped);
            });

        await this.CreateService().ProcessAsync(job);

        await this._jobRepository.Received(1).SetBudgetHeldAsync(
            job.Id,
            Arg.Any<int?>(),
            BudgetScopeKind.ClientMonthly,
            BudgetCapKind.Hard,
            10m,
            Arg.Any<decimal>(),
            Arg.Any<CancellationToken>());
        await this._jobRepository.DidNotReceiveWithAnyArgs().SetFailedAsync(default, default!);
    }

    [Fact]
    public async Task ProcessAsync_NoCapsConfigured_AnswersWithoutReadingASpendBaseline()
    {
        // An installation without the Budgeting capability reports no caps, so nothing is enforced and the
        // baseline query is not paid for on every mention.
        var job = MakeJob();
        SetupAnsweredMention(job, MakeAnswer());

        await this._sut.ProcessAsync(job);

        await this._spendAccumulator.DidNotReceiveWithAnyArgs()
            .GetBaselineAsync(default!, default);
        await this._jobRepository.Received(1)
            .SetCompletedAsync(job.Id, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_SpendRecordingThrows_StillPostsTheAnswer()
    {
        // The tokens are spent either way. Refusing to answer because accounting failed costs the work as
        // well as the number.
        var job = MakeJob();
        SetupAnsweredMention(job, MakeAnswer());
        this._protocolRecorder.BeginForMentionReplyAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<string>())
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("trace store unavailable"));

        await this._sut.ProcessAsync(job);

        await this._threadReplier.Received(1).ReplyAsync(
            job.ClientId,
            job.ReviewThreadReference,
            "An answer.",
            Arg.Any<CancellationToken>());
        await this._jobRepository.Received(1)
            .SetCompletedAsync(job.Id, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await this._jobRepository.DidNotReceiveWithAnyArgs().SetFailedAsync(default, default!);
    }

    [Fact]
    public async Task ProcessAsync_BudgetNoticeCannotBePosted_StillRecordsTheCapThatStoppedIt()
    {
        var job = MakeJob();
        SetupAnsweredMention(job, MakeAnswer());
        this.SetupSpendAgainstMonthlyHardCap(11m);
        this._threadReplier.ReplyAsync(
                Arg.Any<Guid>(),
                Arg.Any<ReviewThreadRef>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs<HttpRequestException>();

        await this.CreateService().ProcessAsync(job);

        await this._jobRepository.Received(1).SetBudgetHeldAsync(
            job.Id,
            Arg.Any<int?>(),
            BudgetScopeKind.ClientMonthly,
            BudgetCapKind.Hard,
            10m,
            11m,
            Arg.Any<CancellationToken>());
    }
}
