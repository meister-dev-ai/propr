// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Application.Features.Budgeting.Models;
using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Threads;
using MeisterDev.ProPR.Application.Features.Reviewing.Threads.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Application.Tests.Features.Reviewing.Threads;

/// <summary>
///     The pass that owns the conversation: it resolves what the developer fixed, answers what they asked,
///     and advances progress only for the threads it actually dealt with.
/// </summary>
public sealed class ThreadPassServiceTests
{
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AuthorizedIdentityId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid ConnectionId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private const string ScopePath = "https://dev.azure.com/org";
    private const string ProjectKey = "project";
    private const string RepositoryId = "repo-1";
    private const int PullRequestId = 42;
    private const int IterationId = 7;
    private const string ThreadId = "17";

    /// <summary>What the developer wrote back. Distinctive, so a test can prove the model was shown it.</summary>
    private const string DeveloperReply = "Fixed, though I kept the null check because the caller can pass null.";

    [Fact]
    public async Task ProcessAsync_RevisionMoved_ResolvesTheFixedThreadAndAdvancesItsProgress()
    {
        var harness = new Harness();
        harness.WithReviewerThread(observedNonReviewerComments: 0);
        harness.WithCodeChangeVerdict(isResolved: true, replyText: "Fixed in the latest change.");

        await harness.RunAsync();

        await harness.StatusWriter.Received(1).UpdateThreadStatusAsync(
            ClientId,
            Arg.Is<ReviewThreadRef>(thread => thread.ExternalThreadId == "17"),
            "fixed",
            Arg.Any<CancellationToken>());
        await harness.PrScans.Received(1).SetThreadPassWatermarkAsync(
            ClientId,
            Arg.Any<string>(),
            Arg.Any<string>(),
            RepositoryId,
            PullRequestId,
            "7",
            Arg.Any<CancellationToken>());
        await harness.ThreadPassJobs.Received(1).SetCompletedAsync(harness.Job.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_ProviderCannotReply_ResolvesTheThreadAndPostsNothing()
    {
        var harness = new Harness();
        harness.WithProviderCapabilities([ReviewThreadCapabilities.Status]);
        harness.WithReviewerThread(observedNonReviewerComments: 0);
        harness.WithCodeChangeVerdict(isResolved: true, replyText: "Fixed in the latest change.");

        await harness.RunAsync();

        await harness.StatusWriter.Received(1).UpdateThreadStatusAsync(
            ClientId,
            Arg.Any<ReviewThreadRef>(),
            "fixed",
            Arg.Any<CancellationToken>());
        await harness.ReplyPublisher.DidNotReceive().ReplyAsync(
            Arg.Any<Guid>(),
            Arg.Any<ReviewThreadRef>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_ProviderCannotReply_AnswersNothingOnAnUnresolvedThread()
    {
        var harness = new Harness();
        harness.WithProviderCapabilities([ReviewThreadCapabilities.Status]);
        harness.WithThreadWatermarkAlreadyAtThisRevision();
        harness.WithReviewerThread(observedNonReviewerComments: 1, storedReplyCount: 0);
        harness.WithConversationalVerdict("Here is why it still stands.");

        await harness.RunAsync();

        await harness.ReplyPublisher.DidNotReceive().ReplyAsync(
            Arg.Any<Guid>(),
            Arg.Any<ReviewThreadRef>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_ThreadAlreadyHandledAtThisCommentCount_MakesNoModelCallAndPublishesNothing()
    {
        var harness = new Harness();
        harness.WithThreadWatermarkAlreadyAtThisRevision();
        harness.WithReviewerThread(observedNonReviewerComments: 1, storedReplyCount: 0);
        harness.WithConversationalVerdict("Here is why it still stands.");
        harness.WithHandledThread(ThreadId, observedReplyCount: 1);

        await harness.RunAsync();

        await harness.ResolutionCore.DidNotReceive().EvaluateConversationalReplyAsync(
            Arg.Any<PrCommentThread>(),
            Arg.Any<IChatClient>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>());
        await harness.ReplyPublisher.DidNotReceive().ReplyAsync(
            Arg.Any<Guid>(),
            Arg.Any<ReviewThreadRef>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_OneThreadFails_KeepsTheHandledThreadsProgressAndLeavesTheWatermark()
    {
        var harness = new Harness();
        harness.WithReviewerThreads(
            CreateThread(ThreadId, nonReviewerComments: 0),
            CreateThread("99", nonReviewerComments: 0));
        harness.WithCodeChangeVerdict(isResolved: true, replyText: "Fixed.");
        harness.StatusWriter.UpdateThreadStatusAsync(
                ClientId,
                Arg.Is<ReviewThreadRef>(thread => thread.ExternalThreadId == "99"),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the provider refused"));

        await harness.RunAsync();

        await harness.PrScans.Received(1).SetLastSeenReplyCountsAsync(
            ClientId,
            Arg.Any<string>(),
            Arg.Any<string>(),
            RepositoryId,
            PullRequestId,
            Arg.Is<IReadOnlyDictionary<string, int>>(counts => counts.ContainsKey(ThreadId)),
            Arg.Any<CancellationToken>());
        await harness.PrScans.DidNotReceive().SetThreadPassWatermarkAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await harness.ThreadPassJobs.Received(1)
            .RecordAttemptFailureAsync(harness.Job.Id, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_PullRequestNoLongerActive_SkipsThePassWithoutTouchingAnyThread()
    {
        var harness = new Harness();
        harness.WithReviewerThread(observedNonReviewerComments: 0);
        harness.WithPullRequestStatus(PrStatus.Abandoned);

        await harness.RunAsync();

        await harness.ThreadPassJobs.Received(1)
            .SetSkippedAsync(harness.Job.Id, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.StatusWriter.DidNotReceive().UpdateThreadStatusAsync(
            Arg.Any<Guid>(),
            Arg.Any<ReviewThreadRef>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_ResolutionDisabledSinceQueueing_TouchesNoThreadAndBlocksNoLaterPass()
    {
        var harness = new Harness();
        harness.WithReviewerThread(observedNonReviewerComments: 0);
        harness.WithCommentResolutionBehavior(CommentResolutionBehavior.Disabled);

        await harness.RunAsync();

        await harness.StatusWriter.DidNotReceive().UpdateThreadStatusAsync(
            Arg.Any<Guid>(),
            Arg.Any<ReviewThreadRef>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());

        // Skipped rather than completed: a completed pass would block the identical trigger, so switching
        // thread interaction back on would be a silent no-op until something else moved.
        await harness.ThreadPassJobs.Received(1)
            .SetSkippedAsync(harness.Job.Id, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await harness.ThreadPassJobs.DidNotReceive()
            .SetCompletedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_ConversationalReply_RecordsTheCommentItPosted()
    {
        var harness = new Harness();
        harness.WithThreadWatermarkAlreadyAtThisRevision();
        harness.WithReviewerThread(observedNonReviewerComments: 1, storedReplyCount: 0);
        harness.WithConversationalVerdict("Here is why it still stands.");
        harness.WithPostedCommentId("reply-comment-7");

        await harness.RunAsync();

        await harness.OriginStore.Received(1).RecordAsync(
            Arg.Is<IReadOnlyList<PostedCommentOriginEntry>>(entries =>
                entries.Count == 1
                && entries[0].ProviderThreadId == "17"
                && entries[0].ProviderCommentId == "reply-comment-7"
                && entries[0].JobId == harness.Job.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_ThreadAlreadyAnsweredAtThisRevision_PublishesNothing()
    {
        var harness = new Harness();
        harness.WithReviewerThread(observedNonReviewerComments: 0);
        harness.WithCodeChangeVerdict(isResolved: true, replyText: "Fixed.");
        harness.WithHandledThread(ThreadId, observedReplyCount: 0, revisionKey: "7");

        await harness.RunAsync();

        await harness.StatusWriter.DidNotReceive().UpdateThreadStatusAsync(
            Arg.Any<Guid>(),
            Arg.Any<ReviewThreadRef>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_RevisionMovedSinceTheThreadWasJudged_JudgesItAgain()
    {
        // The headline promise: push a fix, the finding resolves. Nobody replied to it, so its observed
        // comment count is still zero and only the revision separates this unit of work from the last one.
        var harness = new Harness();
        harness.WithReviewerThread(observedNonReviewerComments: 0);
        harness.WithCodeChangeVerdict(isResolved: true, replyText: "Fixed in the latest change.");
        harness.WithHandledThread(ThreadId, observedReplyCount: 0, revisionKey: "6");

        await harness.RunAsync();

        await harness.ResolutionCore.Received(1).EvaluateCodeChangeAsync(
            Arg.Any<PrCommentThread>(),
            Arg.Any<PullRequest>(),
            Arg.Any<IChatClient>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>(),
            Arg.Any<bool>(),
            Arg.Any<ThreadEvidenceAccess?>());
        await harness.StatusWriter.Received(1).UpdateThreadStatusAsync(
            ClientId,
            Arg.Is<ReviewThreadRef>(thread => thread.ExternalThreadId == "17"),
            "fixed",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_RevisionMovedAndDeveloperReplied_JudgesTheCodeAndAnswersThePersonInOneCall()
    {
        // The scenario the pass exists for: the developer fixes the finding, pushes, and says why they did it
        // that way. Judging the push without reading the reply leaves them talking to nobody.
        var harness = new Harness();
        harness.WithReviewerThread(observedNonReviewerComments: 1, storedReplyCount: 0);
        harness.WithCodeChangeVerdict(isResolved: false, replyText: "The null check is fine, the cast is not.");

        await harness.RunAsync();

        await harness.ResolutionCore.Received(1).EvaluateCodeChangeAsync(
            Arg.Is<PrCommentThread>(thread => thread.Comments.Any(comment => comment.Content == DeveloperReply)),
            Arg.Any<PullRequest>(),
            Arg.Any<IChatClient>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>(),
            true,
            Arg.Any<ThreadEvidenceAccess?>());
        await harness.ResolutionCore.DidNotReceive().EvaluateConversationalReplyAsync(
            Arg.Any<PrCommentThread>(),
            Arg.Any<IChatClient>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>());
        await harness.ReplyPublisher.Received(1).ReplyAsync(
            ClientId,
            Arg.Is<ReviewThreadRef>(thread => thread.ExternalThreadId == ThreadId),
            "The null check is fine, the cast is not.",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_RevisionMovedAndDeveloperReplied_SpendsExactlyOneModelCall()
    {
        // Both conditions fired, and the answer to both comes out of one evaluation. A second call would cost
        // twice and could contradict the first.
        var harness = new Harness();
        harness.WithReviewerThread(observedNonReviewerComments: 1, storedReplyCount: 0);
        harness.WithCodeChangeVerdict(isResolved: false, replyText: "Still standing, and here is why.");

        await harness.RunAsync();

        Assert.Single(harness.ResolutionCore.ReceivedCalls());
    }

    [Fact]
    public async Task ProcessAsync_RevisionMovedAndDeveloperReplied_ResolvesTheThreadAndStillAnswersThem()
    {
        var harness = new Harness();
        harness.WithCommentResolutionBehavior(CommentResolutionBehavior.WithReply);
        harness.WithReviewerThread(observedNonReviewerComments: 1, storedReplyCount: 0);
        harness.WithCodeChangeVerdict(isResolved: true, replyText: "Agreed on the null check, the fix reads well.");

        await harness.RunAsync();

        await harness.ReplyPublisher.Received(1).ReplyAsync(
            ClientId,
            Arg.Is<ReviewThreadRef>(thread => thread.ExternalThreadId == ThreadId),
            "Agreed on the null check, the fix reads well.",
            Arg.Any<CancellationToken>());
        await harness.StatusWriter.Received(1).UpdateThreadStatusAsync(
            ClientId,
            Arg.Is<ReviewThreadRef>(thread => thread.ExternalThreadId == ThreadId),
            "fixed",
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Silent resolution is a choice about narrating a closure, not a licence to ignore a question. A
    ///     developer who fixes a finding and asks why in the same breath was getting the resolution and no
    ///     answer, and the answer had already been generated and paid for before being dropped.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_SilentResolutionAndDeveloperAsked_ResolvesTheThreadAndStillAnswersThem()
    {
        var harness = new Harness();
        harness.WithCommentResolutionBehavior(CommentResolutionBehavior.Silent);
        harness.WithReviewerThread(observedNonReviewerComments: 1, storedReplyCount: 0);
        harness.WithCodeChangeVerdict(isResolved: true, replyText: "It threw once a zero quantity removed an item mid-loop.");

        await harness.RunAsync();

        await harness.ReplyPublisher.Received(1).ReplyAsync(
            ClientId,
            Arg.Is<ReviewThreadRef>(thread => thread.ExternalThreadId == ThreadId),
            "It threw once a zero quantity removed an item mid-loop.",
            Arg.Any<CancellationToken>());
        await harness.StatusWriter.Received(1).UpdateThreadStatusAsync(
            ClientId,
            Arg.Is<ReviewThreadRef>(thread => thread.ExternalThreadId == ThreadId),
            "fixed",
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     And the setting still means what it says: a fix nobody asked about closes without a word, which is
    ///     the whole reason a client chooses silent resolution.
    /// </summary>
    [Fact]
    public async Task ProcessAsync_SilentResolutionAndNobodyAsked_ResolvesTheThreadWithoutSpeaking()
    {
        var harness = new Harness();
        harness.WithCommentResolutionBehavior(CommentResolutionBehavior.Silent);
        harness.WithReviewerThread(observedNonReviewerComments: 0);
        harness.WithCodeChangeVerdict(isResolved: true, replyText: "The fix reads well.");

        await harness.RunAsync();

        await harness.ReplyPublisher.DidNotReceive().ReplyAsync(
            Arg.Any<Guid>(),
            Arg.Any<ReviewThreadRef>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await harness.StatusWriter.Received(1).UpdateThreadStatusAsync(
            ClientId,
            Arg.Is<ReviewThreadRef>(thread => thread.ExternalThreadId == ThreadId),
            "fixed",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_RevisionMovedWithNobodyReplying_JudgesTheThreadAndSaysNothingWhenItStands()
    {
        // Nobody asked the pass anything. An unresolved verdict on a push is not a reason to speak, however
        // much the model had to say.
        var harness = new Harness();
        harness.WithReviewerThread(observedNonReviewerComments: 0);
        harness.WithCodeChangeVerdict(isResolved: false, replyText: "This still looks wrong to me.");

        await harness.RunAsync();

        await harness.ResolutionCore.Received(1).EvaluateCodeChangeAsync(
            Arg.Any<PrCommentThread>(),
            Arg.Any<PullRequest>(),
            Arg.Any<IChatClient>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>(),
            false,
            Arg.Any<ThreadEvidenceAccess?>());
        await harness.ReplyPublisher.DidNotReceive().ReplyAsync(
            Arg.Any<Guid>(),
            Arg.Any<ReviewThreadRef>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await harness.StatusWriter.DidNotReceive().UpdateThreadStatusAsync(
            Arg.Any<Guid>(),
            Arg.Any<ReviewThreadRef>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_DeveloperRepliedWithoutAPush_AnswersTheThreadAsBefore()
    {
        var harness = new Harness();
        harness.WithThreadWatermarkAlreadyAtThisRevision();
        harness.WithReviewerThread(observedNonReviewerComments: 1, storedReplyCount: 0);
        harness.WithConversationalVerdict("Here is why it still stands.");

        await harness.RunAsync();

        await harness.ResolutionCore.Received(1).EvaluateConversationalReplyAsync(
            Arg.Is<PrCommentThread>(thread => thread.Comments.Any(comment => comment.Content == DeveloperReply)),
            Arg.Any<IChatClient>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>());
        await harness.ReplyPublisher.Received(1).ReplyAsync(
            ClientId,
            Arg.Is<ReviewThreadRef>(thread => thread.ExternalThreadId == ThreadId),
            "Here is why it still stands.",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_PublishingThrows_RecordsNoAnswerSoTheThreadIsTriedAgain()
    {
        var harness = new Harness();
        harness.WithReviewerThread(observedNonReviewerComments: 0);
        harness.WithCodeChangeVerdict(isResolved: true, replyText: "Fixed.");
        harness.StatusWriter.UpdateThreadStatusAsync(
                Arg.Any<Guid>(),
                Arg.Any<ReviewThreadRef>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("the provider returned 429"));

        await harness.RunAsync();

        await harness.ThreadPassJobs.DidNotReceive().RecordHandledThreadAsync(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await harness.PrScans.DidNotReceive().SetLastSeenReplyCountsAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<IReadOnlyDictionary<string, int>>(),
            Arg.Any<CancellationToken>());
        await harness.ThreadPassJobs.Received(1)
            .RecordAttemptFailureAsync(harness.Job.Id, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_ThreadAnswered_RecordsTheAnswerOnlyAfterItWasPublished()
    {
        var harness = new Harness();
        harness.WithReviewerThread(observedNonReviewerComments: 0);
        harness.WithCodeChangeVerdict(isResolved: true, replyText: "Fixed.");

        await harness.RunAsync();

        Received.InOrder(() =>
        {
            harness.StatusWriter.UpdateThreadStatusAsync(
                ClientId,
                Arg.Any<ReviewThreadRef>(),
                "fixed",
                Arg.Any<CancellationToken>());
            harness.ThreadPassJobs.RecordHandledThreadAsync(
                harness.Job.Id,
                ClientId,
                ScopePath,
                ProjectKey,
                RepositoryId,
                PullRequestId,
                ThreadId,
                0,
                "7",
                Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task ProcessAsync_ThreadsCouldNotBeRead_LeavesEveryStoredFactWhereItWas()
    {
        // An unreadable thread list is not an empty one. Reading it as empty would record the pull request as
        // checked at this revision and retire the per-thread rows the thread-memory state machine owns, so
        // every already-resolved thread would fire its resolved event again on the next synchronization.
        var harness = new Harness();
        harness.WithUnreadableThreads();

        await harness.RunAsync();

        await harness.PrScans.DidNotReceive().SetThreadPassWatermarkAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await harness.PrScans.DidNotReceive().RetainOnlyThreadsAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<CancellationToken>());
        await harness.ThreadPassJobs.DidNotReceive().SetCompletedAsync(
            Arg.Any<Guid>(),
            Arg.Any<CancellationToken>());
        await harness.ThreadPassJobs.Received(1)
            .RecordAttemptFailureAsync(harness.Job.Id, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_PullRequestGenuinelyHasNoThreads_RetiresTheStoredRowsAndMovesOn()
    {
        var harness = new Harness();

        await harness.RunAsync();

        await harness.PrScans.Received(1).RetainOnlyThreadsAsync(
            ClientId,
            Arg.Any<string>(),
            Arg.Any<string>(),
            RepositoryId,
            PullRequestId,
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.Count == 0),
            Arg.Any<CancellationToken>());
        await harness.ThreadPassJobs.Received(1).SetCompletedAsync(harness.Job.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_OwnershipProvenanceUnavailable_RetiresNoStoredThreadRow()
    {
        // With provenance degraded a thread ProPR does own can read as somebody else's, and the row that
        // would go carries the previous-status half of a resolved transition this pass does not own.
        var harness = new Harness();
        harness.WithFailingProvenanceLookup();

        await harness.RunAsync();

        await harness.PrScans.DidNotReceive().RetainOnlyThreadsAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<CancellationToken>());
        await harness.PrScans.Received(1).SetThreadPassWatermarkAsync(
            ClientId,
            Arg.Any<string>(),
            Arg.Any<string>(),
            RepositoryId,
            PullRequestId,
            "7",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_PassCancelledWhileItRan_StopsBeforeTheNextThread()
    {
        var harness = new Harness();
        harness.WithReviewerThreads(
            CreateThread(ThreadId, nonReviewerComments: 0),
            CreateThread("99", nonReviewerComments: 0));
        harness.WithCodeChangeVerdict(isResolved: true, replyText: "Fixed.");
        harness.WithStoredStatus(ThreadPassJobStatus.Cancelled);

        await harness.RunAsync();

        await harness.StatusWriter.DidNotReceive().UpdateThreadStatusAsync(
            Arg.Any<Guid>(),
            Arg.Any<ReviewThreadRef>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await harness.ThreadPassJobs.DidNotReceive().SetCompletedAsync(
            Arg.Any<Guid>(),
            Arg.Any<CancellationToken>());
        await harness.PrScans.DidNotReceive().SetThreadPassWatermarkAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_ThreadCarriesASystemEntry_CountsItTheWayTheTriggerDoes()
    {
        // The trigger excludes the provider's own activity entries. A pass that counted them would store a
        // number the trigger can never reach, and the reply arm of the trigger would go quiet for good.
        var harness = new Harness();
        harness.WithThreadWatermarkAlreadyAtThisRevision();
        harness.WithReviewerThread(observedNonReviewerComments: 1, storedReplyCount: 0, systemComments: 2);
        harness.WithConversationalVerdict("Here is why it still stands.");

        await harness.RunAsync();

        await harness.PrScans.Received(1).SetLastSeenReplyCountsAsync(
            ClientId,
            Arg.Any<string>(),
            Arg.Any<string>(),
            RepositoryId,
            PullRequestId,
            Arg.Is<IReadOnlyDictionary<string, int>>(counts => counts[ThreadId] == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_RevisionMovedOnBeforeThePassRan_SpendsNothingAndBlocksNoLaterPass()
    {
        var harness = new Harness();
        harness.WithReviewerThread(observedNonReviewerComments: 0);
        harness.WithCodeChangeVerdict(isResolved: true, replyText: "Fixed.");
        harness.WithLatestIteration(IterationId + 1);

        await harness.RunAsync();

        await harness.ResolutionCore.DidNotReceive().EvaluateCodeChangeAsync(
            Arg.Any<PrCommentThread>(),
            Arg.Any<PullRequest>(),
            Arg.Any<IChatClient>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>(),
            Arg.Any<bool>());
        await harness.ThreadPassJobs.Received(1)
            .SetSkippedAsync(harness.Job.Id, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_JudgingACodeChange_LoadsTheAnchorFileDiffAndTheChangedFileNames()
    {
        // One diff is downloaded up front, for the file the thread is anchored to. The names of the other
        // changed files are included so the evaluation can distinguish a fix that was never made from one it
        // was not supplied with, and request the file containing it.
        var harness = new Harness();
        harness.WithReviewerThread(observedNonReviewerComments: 0);
        harness.WithCodeChangeVerdict(isResolved: true, replyText: "Fixed.");

        await harness.RunAsync();

        await harness.PullRequestFetcher.Received(1).FetchThreadContextAsync(
            ScopePath,
            ProjectKey,
            RepositoryId,
            PullRequestId,
            IterationId,
            ClientId,
            Arg.Any<CancellationToken>(),
            true);
        await harness.PullRequestFetcher.Received(1).FetchFileDiffAsync(
            ScopePath,
            ProjectKey,
            RepositoryId,
            PullRequestId,
            IterationId,
            "/src/Foo.cs",
            Arg.Any<int?>(),
            ClientId,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_ChangedFileNamesCannotBeListed_StillJudgesTheThread()
    {
        // Listing the files a pull request changed is the most likely failure on a large one, and it
        // supplements the evaluation rather than being a precondition for it. Its absence must not cost the
        // pass every thread it would otherwise have answered.
        var harness = new Harness();
        harness.WithReviewerThread(observedNonReviewerComments: 0);
        harness.WithUnlistableChangedFiles();
        harness.WithCodeChangeVerdict(isResolved: true, replyText: "Fixed.");

        await harness.RunAsync();

        await harness.ResolutionCore.Received(1).EvaluateCodeChangeAsync(
            Arg.Any<PrCommentThread>(),
            Arg.Any<PullRequest>(),
            Arg.Any<IChatClient>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>(),
            Arg.Any<bool>(),
            Arg.Any<ThreadEvidenceAccess?>());
        await harness.ThreadPassJobs.Received(1).SetCompletedAsync(harness.Job.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_DeveloperRepliedWithoutAPush_DoesNotPayToListChangedFiles()
    {
        // Nothing on the conversational path reads the changed-file names, so the pass does not request
        // them.
        var harness = new Harness();
        harness.WithThreadWatermarkAlreadyAtThisRevision();
        harness.WithReviewerThread(observedNonReviewerComments: 1, storedReplyCount: 0);
        harness.WithConversationalVerdict("Answered.");

        await harness.RunAsync();

        await harness.PullRequestFetcher.DidNotReceive().FetchThreadContextAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>(),
            true);
    }

    [Fact]
    public async Task ProcessAsync_LoadingTheAnchorFile_KeepsTheOtherChangedFileNames()
    {
        // Substituting the anchor file into the changed-file collection must not discard the manifest with
        // it. A pull request that arrived without one derives it from those same changed files, so an
        // unguarded substitution would reduce the manifest to the one file already supplied.
        var harness = new Harness();
        harness.WithReviewerThread(observedNonReviewerComments: 0);
        harness.WithDerivedChangedFileManifest("src/Foo.cs", "src/Service.cs");
        harness.WithCodeChangeVerdict(isResolved: true, replyText: "Fixed.");

        await harness.RunAsync();

        await harness.ResolutionCore.Received(1).EvaluateCodeChangeAsync(
            Arg.Any<PrCommentThread>(),
            Arg.Is<PullRequest>(pr => pr.AllPrFileSummaries.Any(file => file.Path == "src/Service.cs")),
            Arg.Any<IChatClient>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>(),
            Arg.Any<bool>(),
            Arg.Any<ThreadEvidenceAccess?>());
    }

    [Fact]
    public async Task ProcessAsync_JudgingACodeChange_LetsTheEvaluationAskForAnotherFile()
    {
        // The fix for a finding raised on one file is often in another, and an evaluation that cannot be
        // supplied with that file has no way to resolve the thread however many times the developer pushes
        // it.
        var harness = new Harness();
        harness.WithReviewerThread(observedNonReviewerComments: 0);
        harness.WithCodeChangeVerdict(isResolved: true, replyText: "Fixed.");

        await harness.RunAsync();

        await harness.ResolutionCore.Received(1).EvaluateCodeChangeAsync(
            Arg.Any<PrCommentThread>(),
            Arg.Any<PullRequest>(),
            Arg.Any<IChatClient>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>(),
            Arg.Any<bool>(),
            Arg.Is<ThreadEvidenceAccess?>(evidence => evidence != null));
    }

    [Fact]
    public async Task ProcessAsync_LosesTheExecutionClaim_DoesNothing()
    {
        var harness = new Harness();
        harness.WithReviewerThread(observedNonReviewerComments: 0);
        harness.ThreadPassJobs.TryBeginAttemptAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);

        await harness.RunAsync();

        await harness.PullRequestFetcher.DidNotReceive().FetchThreadContextAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<Guid?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<bool>());
    }

    [Fact]
    public async Task ProcessAsync_EvaluatedThread_RecordsItsModelCallAgainstThePassesOwnTrace()
    {
        var harness = new Harness();
        harness.WithReviewerThread(observedNonReviewerComments: 0);
        harness.WithCodeChangeVerdict(isResolved: true, replyText: "Fixed.", inputTokens: 900, outputTokens: 120);
        var protocolId = harness.WithThreadProtocol();

        await harness.RunAsync();

        await harness.ProtocolRecorder.Received(1).BeginForThreadPassAsync(
            harness.Job.Id,
            Arg.Any<int>(),
            "thread-17-code-change",
            "thread-pass-model",
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>());
        await harness.ProtocolRecorder.Received(1).RecordAiCallAsync(
            protocolId,
            1,
            900,
            120,
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<long?>(),
            Arg.Any<CacheCallStatus>(),
            Arg.Any<string?>(),
            Arg.Any<PrefixEligibilityStatus>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<long?>(),
            Arg.Any<long?>());
        await harness.ProtocolRecorder.Received(1).SetCompletedAsync(
            protocolId,
            "Resolved",
            900,
            120,
            1,
            0,
            null,
            Arg.Any<CancellationToken>(),
            Arg.Any<long?>(),
            Arg.Any<CacheObservabilityStatus>(),
            Arg.Any<long?>(),
            Arg.Any<long?>());
        await harness.ThreadPassJobs.Received(1).SetAiConfigAsync(
            harness.Job.Id,
            ConnectionId,
            "thread-pass-model",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_EvaluationThatAskedForMoreCode_RecordsEveryCallAndBillsTheirSum()
    {
        // An evaluation that had to retrieve another file spent two calls. Recording only the first would
        // omit the second from the trace and from what the client is billed.
        var harness = new Harness();
        harness.WithReviewerThread(observedNonReviewerComments: 0);
        harness.WithTwoRoundCodeChangeVerdict();
        var protocolId = harness.WithThreadProtocol();

        await harness.RunAsync();

        await harness.ProtocolRecorder.Received(1).RecordAiCallAsync(
            protocolId,
            1,
            100,
            10,
            Arg.Any<string?>(),
            Arg.Any<string?>(),

            // The reply belongs to the call that produced it, so the earlier one holds none.
            Arg.Is<string?>(sample => sample == null),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<long?>(),
            Arg.Any<CacheCallStatus>(),
            Arg.Any<string?>(),
            Arg.Any<PrefixEligibilityStatus>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<long?>(),
            Arg.Any<long?>());
        await harness.ProtocolRecorder.Received(1).RecordAiCallAsync(
            protocolId,
            2,
            250,
            30,
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Is<string?>(sample => sample == "The service now validates its arguments."),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<long?>(),
            Arg.Any<CacheCallStatus>(),
            Arg.Any<string?>(),
            Arg.Any<PrefixEligibilityStatus>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<long?>(),
            Arg.Any<long?>());
        await harness.ProtocolRecorder.Received(1).SetCompletedAsync(
            protocolId,
            "Resolved",
            350,
            40,
            2,
            0,
            null,
            Arg.Any<CancellationToken>(),
            Arg.Any<long?>(),
            Arg.Any<CacheObservabilityStatus>(),
            Arg.Any<long?>(),
            Arg.Any<long?>());
    }

    [Fact]
    public async Task ProcessAsync_MetersItsCallsAgainstABudgetScopeOfItsOwn()
    {
        var harness = new Harness();
        harness.WithReviewerThread(observedNonReviewerComments: 0);
        harness.WithBudget(new BudgetCaps(null, 100m, null, null, null, null), alreadySpentUsd: 10m);
        harness.WithCodeChangeVerdict(isResolved: true, replyText: "Fixed.");

        await harness.RunAsync();

        await harness.SpendAccumulator.Received(1).GetBaselineAsync(
            Arg.Is<ReviewSpendSubject>(subject =>
                subject.UnitOfWorkId == harness.Job.Id
                && subject.ClientId == ClientId
                && subject.PullRequestId == PullRequestId
                && subject.IterationId == IterationId),
            Arg.Any<DateOnly>(),
            Arg.Any<CancellationToken>());
        Assert.Equal(10m, harness.ObservedScopeBaselineUsd);
    }

    [Fact]
    public async Task ProcessAsync_ClientAlreadyOverACap_HoldsThePassWithoutSpendingOrClaimingIt()
    {
        var harness = new Harness();
        harness.WithReviewerThread(observedNonReviewerComments: 0);
        harness.WithBudget(new BudgetCaps(80m, 100m, null, null, null, null), alreadySpentUsd: 90m);
        harness.WithCodeChangeVerdict(isResolved: true, replyText: "Fixed.");

        await harness.RunAsync();

        await harness.ThreadPassJobs.Received(1).SetBudgetHeldAsync(
            harness.Job.Id,
            BudgetScopeKind.ClientMonthly,
            BudgetCapKind.Soft,
            80m,
            90m,
            Arg.Any<CancellationToken>());
        await harness.ThreadPassJobs.DidNotReceive().TryBeginAttemptAsync(
            Arg.Any<Guid>(),
            Arg.Any<CancellationToken>());
        await harness.ResolutionCore.DidNotReceive().EvaluateCodeChangeAsync(
            Arg.Any<PrCommentThread>(),
            Arg.Any<PullRequest>(),
            Arg.Any<IChatClient>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>(),
            Arg.Any<bool>());
        await harness.BudgetEventPublisher.Received(1).PublishAsync(
            Arg.Any<BudgetEventNotification>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_HardCapReachedPartWay_StopsThePassAndKeepsTheThreadItAlreadyAnswered()
    {
        var harness = new Harness();
        harness.WithReviewerThreads(
            CreateThread(ThreadId, nonReviewerComments: 0),
            CreateThread("99", nonReviewerComments: 0));
        harness.WithBudget(new BudgetCaps(null, null, null, null, null, 5m), alreadySpentUsd: 0m);
        harness.WithCodeChangeVerdict(isResolved: true, replyText: "Fixed.");
        harness.WithHardCapTrippedOnThread("99");

        await harness.RunAsync();

        await harness.ThreadPassJobs.Received(1).SetBudgetExceededAsync(
            harness.Job.Id,
            BudgetScopeKind.Increment,
            BudgetCapKind.Hard,
            5m,
            6m,
            Arg.Any<CancellationToken>());
        await harness.PrScans.Received(1).SetLastSeenReplyCountsAsync(
            ClientId,
            Arg.Any<string>(),
            Arg.Any<string>(),
            RepositoryId,
            PullRequestId,
            Arg.Is<IReadOnlyDictionary<string, int>>(counts => counts.ContainsKey(ThreadId)),
            Arg.Any<CancellationToken>());
        await harness.PrScans.DidNotReceive().SetThreadPassWatermarkAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_RecordingWhatAThreadSpentThrows_StillResolvesTheThread()
    {
        var harness = new Harness();
        harness.WithReviewerThread(observedNonReviewerComments: 0);
        harness.WithCodeChangeVerdict(isResolved: true, replyText: "Fixed.");
        harness.WithThreadProtocol();
        harness.WithFailingSpendRecording();

        await harness.RunAsync();

        await harness.StatusWriter.Received(1).UpdateThreadStatusAsync(
            ClientId,
            Arg.Is<ReviewThreadRef>(thread => thread.ExternalThreadId == "17"),
            "fixed",
            Arg.Any<CancellationToken>());
        await harness.ThreadPassJobs.Received(1).SetCompletedAsync(harness.Job.Id, Arg.Any<CancellationToken>());
    }

    private static PrCommentThread CreateThread(string threadId, int nonReviewerComments, int systemComments = 0)
    {
        var commentIdBase = long.Parse(threadId, CultureInfo.InvariantCulture) * 100;
        var comments = new List<PrThreadComment>
        {
            new("Bot", "Please fix this.", AuthorizedIdentityId, commentIdBase),
        };

        for (var index = 0; index < nonReviewerComments; index++)
        {
            comments.Add(new PrThreadComment("Dev", DeveloperReply, Guid.NewGuid(), commentIdBase + index + 1));
        }

        for (var index = 0; index < systemComments; index++)
        {
            comments.Add(
                new PrThreadComment(
                    "Azure DevOps",
                    "updated the source branch",
                    Guid.NewGuid(),
                    commentIdBase + nonReviewerComments + index + 1,
                    IsSystemGenerated: true));
        }

        return new PrCommentThread(threadId, "/src/Foo.cs", 10, comments.AsReadOnly());
    }

    private sealed class Harness
    {
        private readonly IClientRegistry _clientRegistry = Substitute.For<IClientRegistry>();
        private readonly IScmProviderRegistry _providerRegistry = Substitute.For<IScmProviderRegistry>();
        private readonly IAiRuntimeResolver _aiRuntimeResolver = Substitute.For<IAiRuntimeResolver>();
        private readonly BudgetScopeAccessor _budgetScopeAccessor = new();

        private readonly IPullRequestIterationResolver _iterationResolver =
            Substitute.For<IPullRequestIterationResolver>();

        private readonly ReviewPrScan _scan;
        private readonly List<PrCommentThread> _threads = [];
        private PrStatus _pullRequestStatus = PrStatus.Active;
        private bool _threadsAreReadable = true;
        private bool _changedFilesAreUnlistable;
        private IReadOnlyList<ChangedFile> _changedFiles = [];

        public Harness()
        {
            this.Job = new ThreadPassJob(
                Guid.NewGuid(),
                ClientId,
                ScopePath,
                ProjectKey,
                RepositoryId,
                PullRequestId,
                IterationId,
                "7",
                "7|abc");

            this._scan = new ReviewPrScan(Guid.NewGuid(), ClientId, "https://provider.example", "project", RepositoryId, PullRequestId, "7")
            {
                LastThreadPassRevisionKey = "6",
            };

            this._clientRegistry.GetCommentResolutionBehaviorAsync(ClientId, Arg.Any<CancellationToken>())
                .Returns(CommentResolutionBehavior.Silent);
            this._providerRegistry.GetRegisteredCapabilities(ScmProvider.AzureDevOps)
                .Returns([ReviewThreadCapabilities.Status, ReviewThreadCapabilities.Reply]);
            this._providerRegistry.GetReviewThreadStatusWriter(ScmProvider.AzureDevOps).Returns(this.StatusWriter);
            this._providerRegistry.GetReviewThreadReplyPublisher(ScmProvider.AzureDevOps).Returns(this.ReplyPublisher);

            this.BudgetCapsProvider.GetCapsAsync(ClientId, Arg.Any<CancellationToken>()).Returns(BudgetCaps.None);

            this.ThreadPassJobs.TryBeginAttemptAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
            this.ThreadPassJobs.GetHandledThreadKeysAsync(
                    ClientId,
                    ScopePath,
                    ProjectKey,
                    RepositoryId,
                    PullRequestId,
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>())
                .Returns([]);
            this.ThreadPassJobs.RecordAttemptFailureAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>())
                .Returns(true);
            this.ThreadPassJobs.SetCompletedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
            this._iterationResolver.GetLatestIterationIdAsync(
                    ClientId,
                    ScopePath,
                    ProjectKey,
                    RepositoryId,
                    PullRequestId,
                    Arg.Any<CancellationToken>())
                .Returns(IterationId);

            this.OriginStore.GetJobIdsForPullRequestAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<long>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<PostedCommentOriginRow>>([]));

            this.ReplyPublisher.Provider.Returns(ScmProvider.AzureDevOps);
            this.ReplyPublisher.ReplyAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<ReviewThreadRef>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<string?>(null));

            var runtime = Substitute.For<IResolvedAiChatRuntime>();
            runtime.ChatClient.Returns(Substitute.For<IChatClient>());
            runtime.Model.Returns(
                new AiConfiguredModelDto(
                    Guid.NewGuid(),
                    "thread-pass-model",
                    "thread-pass-model",
                    [AiOperationKind.Chat],
                    [AiProtocolMode.Auto]));
            runtime.Connection.Returns(
                new AiConnectionDto(
                    ConnectionId,
                    ClientId,
                    "Thread pass connection",
                    AiProviderKind.AzureOpenAi,
                    "https://test.openai.azure.com",
                    AiAuthMode.ApiKey,
                    AiDiscoveryMode.ManualOnly,
                    true,
                    [],
                    [],
                    AiVerificationResultDto.NeverVerified,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow));
            this._aiRuntimeResolver.ResolveChatRuntimeAsync(
                    ClientId,
                    AiPurpose.ReviewDefault,
                    Arg.Any<CancellationToken>())
                .Returns(runtime);

            this.ApplyScan();
        }

        public ThreadPassJob Job { get; }

        public IThreadPassJobRepository ThreadPassJobs { get; } = Substitute.For<IThreadPassJobRepository>();

        public IReviewPrScanThreadPassStore PrScans { get; } = Substitute.For<IReviewPrScanThreadPassStore>();

        public IPullRequestFetcher PullRequestFetcher { get; } = Substitute.For<IPullRequestFetcher>();

        public IAiCommentResolutionCore ResolutionCore { get; } = Substitute.For<IAiCommentResolutionCore>();

        public IReviewThreadStatusWriter StatusWriter { get; } = Substitute.For<IReviewThreadStatusWriter>();

        public IReviewThreadReplyPublisher ReplyPublisher { get; } = Substitute.For<IReviewThreadReplyPublisher>();

        public IPostedCommentOriginStore OriginStore { get; } = Substitute.For<IPostedCommentOriginStore>();

        public IProtocolRecorder ProtocolRecorder { get; } = Substitute.For<IProtocolRecorder>();

        public IBudgetCapsProvider BudgetCapsProvider { get; } = Substitute.For<IBudgetCapsProvider>();

        public IReviewSpendAccumulator SpendAccumulator { get; } = Substitute.For<IReviewSpendAccumulator>();

        public IBudgetEventPublisher BudgetEventPublisher { get; } = Substitute.For<IBudgetEventPublisher>();

        /// <summary>The increment baseline the ambient budget scope carried while the model was being called.</summary>
        public decimal? ObservedScopeBaselineUsd { get; private set; }

        /// <summary>Gives the client caps and a baseline spend, so the pass meters itself against them.</summary>
        public void WithBudget(BudgetCaps caps, decimal alreadySpentUsd)
        {
            this.BudgetCapsProvider.GetCapsAsync(ClientId, Arg.Any<CancellationToken>()).Returns(caps);
            this.SpendAccumulator.GetBaselineAsync(
                    Arg.Any<ReviewSpendSubject>(),
                    Arg.Any<DateOnly>(),
                    Arg.Any<CancellationToken>())
                .Returns(
                    new ReviewSpendBaseline(
                        new ReviewScopeSpend(alreadySpentUsd, false),
                        new ReviewScopeSpend(alreadySpentUsd, false),
                        new ReviewScopeSpend(alreadySpentUsd, false)));
        }

        /// <summary>Makes the model call for one thread trip a hard cap, as the enforcing chat client does.</summary>
        public void WithHardCapTrippedOnThread(string threadId)
        {
            var breach = new BudgetBreach(BudgetScopeKind.Increment, BudgetCapKind.Hard, 5m, 6m);
            this.ResolutionCore.EvaluateCodeChangeAsync(
                    Arg.Is<PrCommentThread>(thread => thread.ThreadId == threadId),
                    Arg.Any<PullRequest>(),
                    Arg.Any<IChatClient>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<string?>(),
                    Arg.Any<bool>(),
                    Arg.Any<ThreadEvidenceAccess?>())
                .ThrowsAsync(new BudgetHardCapReachedException(breach));
        }

        /// <summary>Gives the recorder a trace record to hand back, and returns its identifier.</summary>
        public Guid WithThreadProtocol()
        {
            var protocolId = Guid.NewGuid();
            this.ProtocolRecorder.BeginForThreadPassAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<int>(),
                    Arg.Any<string?>(),
                    Arg.Any<string?>(),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<string?>())
                .Returns(protocolId);
            return protocolId;
        }

        /// <summary>Makes closing the trace record throw, which is what a failed usage write looks like here.</summary>
        public void WithFailingSpendRecording()
        {
            this.ProtocolRecorder.SetCompletedAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<long>(),
                    Arg.Any<long>(),
                    Arg.Any<int>(),
                    Arg.Any<int>(),
                    Arg.Any<int?>(),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<long?>(),
                    Arg.Any<CacheObservabilityStatus>(),
                    Arg.Any<long?>(),
                    Arg.Any<long?>())
                .ThrowsAsync(new InvalidOperationException("the usage write failed"));
        }

        public void WithReviewerThread(int observedNonReviewerComments, int storedReplyCount = 0, int systemComments = 0)
        {
            this._scan.Threads.Add(
                new ReviewPrScanThread
                {
                    ReviewPrScanId = this._scan.Id,
                    ThreadId = ThreadId,
                    LastSeenReplyCount = storedReplyCount,
                });
            this.ApplyScan();
            this._threads.Add(CreateThread(ThreadId, observedNonReviewerComments, systemComments));
        }

        public void WithReviewerThreads(params PrCommentThread[] threads)
        {
            this._threads.AddRange(threads);
        }

        public void WithThreadWatermarkAlreadyAtThisRevision()
        {
            this._scan.LastThreadPassRevisionKey = "7";
            this.ApplyScan();
        }

        /// <summary>The provider could not be asked what the threads are, which is not the same as there being none.</summary>
        public void WithUnreadableThreads()
        {
            this._threadsAreReadable = false;
        }

        /// <summary>Makes the posting-provenance read fail, which degrades ownership to identity alone.</summary>
        public void WithFailingProvenanceLookup()
        {
            this.OriginStore.GetJobIdsForPullRequestAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<long>(),
                    Arg.Any<CancellationToken>())
                .ThrowsAsync(new InvalidOperationException("the provenance store is unreachable"));
        }

        /// <summary>Says what the pass's own row now reads, which is how a cancellation reaches a running pass.</summary>
        public void WithStoredStatus(ThreadPassJobStatus status)
        {
            var stored = new ThreadPassJob(
                this.Job.Id,
                ClientId,
                ScopePath,
                ProjectKey,
                RepositoryId,
                PullRequestId,
                IterationId,
                "7",
                "7|abc")
            {
                Status = status,
            };
            this.ThreadPassJobs.GetByIdAsync(this.Job.Id, Arg.Any<CancellationToken>()).Returns(stored);
        }

        /// <summary>Puts the pull request at a later revision than the one the pass was queued at.</summary>
        public void WithLatestIteration(int iterationId)
        {
            this._iterationResolver.GetLatestIterationIdAsync(
                    ClientId,
                    ScopePath,
                    ProjectKey,
                    RepositoryId,
                    PullRequestId,
                    Arg.Any<CancellationToken>())
                .Returns(iterationId);
        }

        /// <summary>
        ///     Puts one already-answered thread in front of the pass. The row is handed back whatever revision
        ///     is asked for, so what decides is the pass's own comparison rather than the stub's.
        /// </summary>
        public void WithHandledThread(string threadId, int observedReplyCount, string revisionKey = "7")
        {
            this.ThreadPassJobs.GetHandledThreadKeysAsync(
                    ClientId,
                    ScopePath,
                    ProjectKey,
                    RepositoryId,
                    PullRequestId,
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>())
                .Returns([new ThreadPassHandledThreadKey(threadId, observedReplyCount, revisionKey)]);
        }

        public void WithCommentResolutionBehavior(CommentResolutionBehavior behavior)
        {
            this._clientRegistry.GetCommentResolutionBehaviorAsync(ClientId, Arg.Any<CancellationToken>())
                .Returns(behavior);
        }

        public void WithProviderCapabilities(IReadOnlyList<string> capabilities)
        {
            this._providerRegistry.GetRegisteredCapabilities(ScmProvider.AzureDevOps).Returns(capabilities);
        }

        public void WithPullRequestStatus(PrStatus status)
        {
            this._pullRequestStatus = status;
        }

        public void WithPostedCommentId(string commentId)
        {
            this.ReplyPublisher.ReplyAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<ReviewThreadRef>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<string?>(commentId));
        }

        public void WithCodeChangeVerdict(
            bool isResolved,
            string? replyText,
            long? inputTokens = null,
            long? outputTokens = null)
        {
            this.ResolutionCore.EvaluateCodeChangeAsync(
                    Arg.Any<PrCommentThread>(),
                    Arg.Any<PullRequest>(),
                    Arg.Any<IChatClient>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<string?>(),
                    Arg.Any<bool>(),
                    Arg.Any<ThreadEvidenceAccess?>())
                .Returns(_ =>
                {
                    // Read from inside the call, which is where the enforcing chat client reads it too.
                    this.ObservedScopeBaselineUsd = this._budgetScopeAccessor.Current?.Baseline.Increment.KnownUsd;
                    return new ThreadResolutionResult(isResolved, replyText, inputTokens, outputTokens);
                });
        }

        /// <summary>A provider that cannot complete the changed-file listing and reports that by throwing.</summary>
        public void WithUnlistableChangedFiles()
        {
            this._changedFilesAreUnlistable = true;
        }

        /// <summary>
        ///     Names the files this pull request changed in the form used by a provider that reports no
        ///     separate manifest: as changed files, from which the manifest is derived.
        /// </summary>
        public void WithDerivedChangedFileManifest(params string[] paths)
        {
            this._changedFiles = paths
                .Select(path => new ChangedFile(path, ChangeType.Edit, "content", "diff"))
                .ToList()
                .AsReadOnly();
        }

        /// <summary>A verdict reached only after the evaluation requested a file it was not first supplied with.</summary>
        public void WithTwoRoundCodeChangeVerdict()
        {
            this.ResolutionCore.EvaluateCodeChangeAsync(
                    Arg.Any<PrCommentThread>(),
                    Arg.Any<PullRequest>(),
                    Arg.Any<IChatClient>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<string?>(),
                    Arg.Any<bool>(),
                    Arg.Any<ThreadEvidenceAccess?>())
                .Returns(
                    new ThreadResolutionResult(
                        true,
                        "The service now validates its arguments.",
                        350,
                        40,
                        Calls:
                        [
                            new ThreadResolutionCall(100, 10),
                            new ThreadResolutionCall(250, 30),
                        ]));
        }

        public void WithConversationalVerdict(string replyText)
        {
            this.ResolutionCore.EvaluateConversationalReplyAsync(
                    Arg.Any<PrCommentThread>(),
                    Arg.Any<IChatClient>(),
                    Arg.Any<string>(),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<string?>())
                .Returns(new ThreadResolutionResult(false, replyText));
        }

        public Task RunAsync()
        {
            this.PullRequestFetcher.FetchThreadContextAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<int>(),
                    Arg.Any<int>(),
                    Arg.Any<Guid?>(),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<bool>())
                .Returns(this.CreatePullRequest());

            if (this._changedFilesAreUnlistable)
            {
                this.PullRequestFetcher.FetchThreadContextAsync(
                        Arg.Any<string>(),
                        Arg.Any<string>(),
                        Arg.Any<string>(),
                        Arg.Any<int>(),
                        Arg.Any<int>(),
                        Arg.Any<Guid?>(),
                        Arg.Any<CancellationToken>(),
                        true)
                    .ThrowsAsync(new InvalidOperationException("the changed-file listing was truncated."));
            }

            this.PullRequestFetcher.FetchFileDiffAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<int>(),
                    Arg.Any<int>(),
                    Arg.Any<string>(),
                    Arg.Any<int?>(),
                    Arg.Any<Guid?>(),
                    Arg.Any<CancellationToken>())
                .Returns(new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", "diff"));

            var sut = new ThreadPassService(
                this.ThreadPassJobs,
                this.PrScans,
                this.PullRequestFetcher,
                this._providerRegistry,
                this._clientRegistry,
                this.ResolutionCore,
                this.ProtocolRecorder,
                NullLogger<ThreadPassService>.Instance,
                this._aiRuntimeResolver,
                iterationResolver: this._iterationResolver,
                postedCommentOriginStore: this.OriginStore,
                budgetCapsProvider: this.BudgetCapsProvider,
                spendAccumulator: this.SpendAccumulator,
                budgetScopeAccessor: this._budgetScopeAccessor,
                budgetEventPublisher: this.BudgetEventPublisher);

            return sut.ProcessAsync(this.Job, CancellationToken.None);
        }

        private PullRequest CreatePullRequest()
        {
            return new PullRequest(
                ScopePath,
                ProjectKey,
                RepositoryId,
                "repo",
                PullRequestId,
                IterationId,
                "Title",
                null,
                "feature",
                "main",
                this._changedFiles,
                this._pullRequestStatus,
                this._threadsAreReadable ? this._threads.AsReadOnly() : null,
                null,
                AuthorizedIdentityId,
                "review-bot");
        }

        private void ApplyScan()
        {
            this.PrScans.GetAsync(ClientId, Arg.Any<string>(), Arg.Any<string>(), RepositoryId, PullRequestId, Arg.Any<CancellationToken>())
                .Returns(this._scan);
        }
    }
}
