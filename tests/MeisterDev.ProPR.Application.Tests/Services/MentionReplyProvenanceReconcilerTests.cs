// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Mentions.Models;
using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterDev.ProPR.Application.Tests.Services;

/// <summary>
///     Unit tests for <see cref="MentionReplyProvenanceReconciler" />: rewriting the provenance of mention
///     answers that reached a pull request without it.
/// </summary>
public sealed class MentionReplyProvenanceReconcilerTests
{
    private static readonly Guid ClientId = Guid.NewGuid();

    private readonly IMentionReplyJobRepository _jobRepository = Substitute.For<IMentionReplyJobRepository>();
    private readonly IPostedCommentOriginStore _originStore = Substitute.For<IPostedCommentOriginStore>();
    private readonly MentionReplyProvenanceReconciler _sut;

    public MentionReplyProvenanceReconcilerTests()
    {
        this._jobRepository.GetPostedRepliesAsync(
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PostedMentionReply>>([]));
        this._originStore.GetJobIdsWithOriginsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>([]));

        this._sut = new MentionReplyProvenanceReconciler(
            this._jobRepository,
            this._originStore,
            NullLogger<MentionReplyProvenanceReconciler>.Instance);
    }

    [Fact]
    public async Task ReconcileAsync_AnswerPostedWithoutProvenance_RecordsItAgainstTheJobThatPostedIt()
    {
        // The case the whole recovery exists for: the answer is on the pull request, the job is complete, and
        // the origin row never got written. Every coordinate the row needs is on the job, so nothing has to be
        // asked of the provider and no comment has to be guessed at.
        var postedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var reply = MakeReply(providerCommentId: "answer-comment-5", postedAt: postedAt);
        this.SetupPostedReplies(reply);

        var recovered = await this._sut.ReconcileAsync();

        Assert.Equal(1, recovered);
        await this._originStore.Received(1).RecordAsync(
            Arg.Is<IReadOnlyList<PostedCommentOriginEntry>>(entries =>
                entries.Count == 1
                && entries[0].ClientId == reply.ClientId
                && entries[0].RepositoryId == reply.RepositoryId
                && entries[0].PullRequestId == reply.PullRequestId
                && entries[0].ProviderThreadId == reply.ProviderThreadId
                && entries[0].ProviderCommentId == "answer-comment-5"
                && entries[0].JobId == reply.JobId
                && entries[0].PostedAt == postedAt),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_AnswerAlreadyRecorded_LeavesItAlone()
    {
        // The expected state on every restart. Re-recording would be harmless, but it would also overwrite the
        // row's posted timestamp on every start of the process, and report recoveries that never happened.
        var reply = MakeReply();
        this.SetupPostedReplies(reply);
        this.SetupAlreadyRecorded(reply.JobId);

        var recovered = await this._sut.ReconcileAsync();

        Assert.Equal(0, recovered);
        await this._originStore.DidNotReceiveWithAnyArgs().RecordAsync(default!);
    }

    [Fact]
    public async Task ReconcileAsync_OnlySomeAnswersLostTheirRow_RecordsExactlyThoseThatDid()
    {
        var recorded = MakeReply(providerCommentId: "recorded-1");
        var lost = MakeReply(providerCommentId: "lost-1");
        this.SetupPostedReplies(recorded, lost);
        this.SetupAlreadyRecorded(recorded.JobId);

        var recovered = await this._sut.ReconcileAsync();

        Assert.Equal(1, recovered);
        await this._originStore.Received(1).RecordAsync(
            Arg.Is<IReadOnlyList<PostedCommentOriginEntry>>(entries =>
                entries.Count == 1 && entries[0].JobId == lost.JobId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_NoAnswersToConsider_TouchesTheProvenanceStoreNotAtAll()
    {
        var recovered = await this._sut.ReconcileAsync();

        Assert.Equal(0, recovered);
        await this._originStore.DidNotReceiveWithAnyArgs()
            .GetJobIdsWithOriginsAsync(default!);
        await this._originStore.DidNotReceiveWithAnyArgs().RecordAsync(default!);
    }

    [Fact]
    public async Task ReconcileAsync_ReadsOnlyRecentAnswersAndCapsThePass()
    {
        // Both bounds matter. An answer old enough that its pull request's retained data is gone has nothing
        // left to attribute, and a pass that finds an implausible number of them is a signal to read rather
        // than a queue to work through.
        var before = DateTimeOffset.UtcNow;

        await this._sut.ReconcileAsync();

        await this._jobRepository.Received(1).GetPostedRepliesAsync(
            Arg.Is<DateTimeOffset>(cutoff => cutoff > before.AddDays(-8) && cutoff < before.AddDays(-6)),
            Arg.Is<int>(maxResults => maxResults > 0 && maxResults <= 1000),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconcileAsync_ProvenanceStoreUnavailable_SurfacesTheFailureToItsCaller()
    {
        // The reconciler does not swallow: its caller decides that a recovery pass which could not run is
        // logged and dropped, so the work it was recovering for is not silently reported as done.
        this.SetupPostedReplies(MakeReply());
        this._originStore.GetJobIdsWithOriginsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("provenance store unavailable"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => this._sut.ReconcileAsync());
    }

    private static PostedMentionReply MakeReply(
        string providerCommentId = "answer-comment-1",
        string providerThreadId = "10",
        DateTimeOffset? postedAt = null)
    {
        return new PostedMentionReply(
            Guid.NewGuid(),
            ClientId,
            "repo",
            42,
            providerThreadId,
            providerCommentId,
            postedAt ?? DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    private void SetupPostedReplies(params PostedMentionReply[] replies)
    {
        this._jobRepository.GetPostedRepliesAsync(
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<PostedMentionReply>>(replies));
    }

    private void SetupAlreadyRecorded(params Guid[] jobIds)
    {
        this._originStore.GetJobIdsWithOriginsAsync(Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>(jobIds));
    }
}
