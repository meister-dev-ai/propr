// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Tests.Features.ThreadOwnership;

/// <summary>
///     The one published answer to "is this thread ours?". Provenance decides first; the authenticated token
///     identity is the fallback for threads posted before provenance existed or whose row is missing. Nothing
///     else is an input, and in particular the configured reviewer identity is not.
/// </summary>
public sealed class ThreadOwnershipResolverTests
{
    private static readonly Guid TokenIdentityId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ConfiguredReviewerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid HumanAuthorId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid PostingJobId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    [Fact]
    public void OwnsThread_ProvenanceRecordsTheFirstComment_IsOwnedWhoeverAuthoredIt()
    {
        var sut = ThreadOwnershipResolver.Create(
            [new PostedCommentOriginRow("17", "100", PostingJobId)],
            ThreadOwnerIdentity.None,
            ProviderCommentIdScope.Thread);

        // The author is an account nothing else recognises. ProPR recorded posting the comment, so the
        // thread is ProPR's regardless of which account the token went out as.
        Assert.True(sut.OwnsThread(new ThreadCommentRef("17", "100", HumanAuthorId, "someone-else")));
    }

    [Fact]
    public void OwnsThread_NoProvenanceButFirstCommentIsTheTokenIdentity_IsOwned()
    {
        var sut = ThreadOwnershipResolver.Create(
            [],
            new ThreadOwnerIdentity(TokenIdentityId),
            ProviderCommentIdScope.Thread);

        Assert.True(sut.OwnsThread(new ThreadCommentRef("17", "100", TokenIdentityId, null)));
    }

    [Fact]
    public void OwnsThread_NoProvenanceAndFirstCommentIsHuman_IsNotOwned()
    {
        var sut = ThreadOwnershipResolver.Create(
            [],
            new ThreadOwnerIdentity(TokenIdentityId),
            ProviderCommentIdScope.Thread);

        Assert.False(sut.OwnsThread(new ThreadCommentRef("17", "100", HumanAuthorId, "jane")));
    }

    [Fact]
    public void OwnsThread_FirstCommentIsTheConfiguredReviewerAndNotTheToken_IsNotOwned()
    {
        // The deliberate narrowing. The configured reviewer identity says which pull requests to review; it
        // is not who posts, and it is no longer an ownership input. A thread it authored, with no provenance
        // row, belongs to whoever asks about non-reviewer threads.
        var sut = ThreadOwnershipResolver.Create(
            [],
            new ThreadOwnerIdentity(TokenIdentityId),
            ProviderCommentIdScope.Thread);

        Assert.False(sut.OwnsThread(new ThreadCommentRef("17", "100", ConfiguredReviewerId, "review-bot")));
    }

    [Fact]
    public void OwnsThread_NoIdentityResolvable_ProvenanceStillDecides()
    {
        var sut = ThreadOwnershipResolver.Create(
            [new PostedCommentOriginRow("17", "100", PostingJobId)],
            ThreadOwnerIdentity.None,
            ProviderCommentIdScope.Thread);

        Assert.True(sut.OwnsThread(new ThreadCommentRef("17", "100", HumanAuthorId, "someone-else")));
        Assert.False(sut.OwnsThread(new ThreadCommentRef("18", "200", HumanAuthorId, "someone-else")));
    }

    [Fact]
    public void OwnsThread_LoginBasedProvider_MatchesTheTokenLoginCaseInsensitively()
    {
        var sut = ThreadOwnershipResolver.Create(
            [],
            new ThreadOwnerIdentity(Login: "meister-dev"),
            ProviderCommentIdScope.PullRequest);

        Assert.True(sut.OwnsThread(new ThreadCommentRef(null, "100", null, "Meister-Dev")));
        Assert.False(sut.OwnsThread(new ThreadCommentRef(null, "101", null, "octocat")));
    }

    [Fact]
    public void OwnsThread_ThreadScopedCommentIds_OneRecordedCommentDoesNotClaimEveryThread()
    {
        // Azure DevOps numbers a comment within its thread, so the first comment of every thread on the pull
        // request is comment 1. A review that posted nothing but a summary leaves exactly one row at comment
        // id 1, and matching on the comment id alone would hand it every human thread on the pull request:
        // each one evaluated at the cost of a model call, and under the reply-on-resolve behaviour answered
        // and closed.
        var sut = ThreadOwnershipResolver.Create(
            [new PostedCommentOriginRow("17", "1", PostingJobId)],
            ThreadOwnerIdentity.None,
            ProviderCommentIdScope.Thread);

        Assert.True(sut.OwnsThread(new ThreadCommentRef("17", "1", HumanAuthorId, null)));
        Assert.False(sut.OwnsThread(new ThreadCommentRef("18", "1", HumanAuthorId, "jane")));
        Assert.Null(sut.ResolveOriginatingJobId("18", "1"));
    }

    [Fact]
    public void OwnsThread_ThreadScopedCommentIds_WithNoThreadIdToMatchOn_IsNotOwned()
    {
        // Half an identity is no identity: a thread-scoped comment number with no thread beside it could be
        // any thread's first comment, so it is not evidence that this one is ProPR's.
        var sut = ThreadOwnershipResolver.Create(
            [new PostedCommentOriginRow("17", "1", PostingJobId)],
            ThreadOwnerIdentity.None,
            ProviderCommentIdScope.Thread);

        Assert.False(sut.OwnsThread(new ThreadCommentRef(null, "1", HumanAuthorId, null)));
    }

    [Fact]
    public void OwnsComment_TwoThreadsShareACommentId_TheThreadIdDisambiguates()
    {
        // Azure DevOps scopes comment ids to a thread, so one pull request can hold several origins under
        // one comment id. Only the origin whose thread matches is this comment's.
        var sut = ThreadOwnershipResolver.Create(
            [
                new PostedCommentOriginRow("17", "1", PostingJobId),
                new PostedCommentOriginRow("18", "1", PostingJobId),
            ],
            ThreadOwnerIdentity.None,
            ProviderCommentIdScope.Thread);

        Assert.True(sut.OwnsComment(new ThreadCommentRef("17", "1", HumanAuthorId, null)));
        Assert.True(sut.OwnsComment(new ThreadCommentRef("18", "1", HumanAuthorId, null)));
        Assert.False(sut.OwnsComment(new ThreadCommentRef("19", "1", HumanAuthorId, null)));
    }

    [Fact]
    public void OwnsComment_CommentIdIsUniqueWithinThePullRequest_ResolvesWithoutAMatchingThreadId()
    {
        // GitHub, GitLab and Forgejo record the review or discussion id as the provider thread id while the
        // crawl reports a different one. Their comment ids are unique within the pull request, so the comment
        // id decides alone and the thread id is ignored.
        var sut = ThreadOwnershipResolver.Create(
            [new PostedCommentOriginRow("review-9", "100", PostingJobId)],
            ThreadOwnerIdentity.None,
            ProviderCommentIdScope.PullRequest);

        Assert.True(sut.OwnsComment(new ThreadCommentRef("17", "100", HumanAuthorId, "someone-else")));
    }

    [Fact]
    public void OwnsComment_ReplyFromAHumanOnAnOwnedThread_IsNotOwned()
    {
        // What the reply count is built from: the thread is ProPR's, the reply on it is not.
        var sut = ThreadOwnershipResolver.Create(
            [new PostedCommentOriginRow("17", "100", PostingJobId)],
            new ThreadOwnerIdentity(TokenIdentityId),
            ProviderCommentIdScope.Thread);

        Assert.False(sut.OwnsComment(new ThreadCommentRef("17", "101", HumanAuthorId, "jane")));
    }

    [Fact]
    public void ResolveOriginatingJobId_ReturnsTheJobThatPostedTheComment()
    {
        var sut = ThreadOwnershipResolver.Create(
            [new PostedCommentOriginRow("17", "100", PostingJobId)],
            ThreadOwnerIdentity.None,
            ProviderCommentIdScope.Thread);

        Assert.Equal(PostingJobId, sut.ResolveOriginatingJobId("17", "100"));
        Assert.Null(sut.ResolveOriginatingJobId("17", "101"));
    }

    [Fact]
    public void ContributeIdentity_KeepsProvenanceAndAddsTheIdentityOnlyTheProviderCanResolve()
    {
        // A provider adapter is handed the pass's provenance and contributes the identity its own connection
        // handshake resolved, which no caller above it can obtain. It contributes into the instance it was
        // handed, so every consumer that comes after it in the same pass reads the same answer.
        var sut = ThreadOwnershipResolver.Create(
            [new PostedCommentOriginRow("17", "100", PostingJobId)],
            ThreadOwnerIdentity.None,
            ProviderCommentIdScope.Thread);

        sut.ContributeIdentity(new ThreadOwnerIdentity(TokenIdentityId));

        Assert.True(sut.OwnsThread(new ThreadCommentRef("17", "100", HumanAuthorId, null)));
        Assert.True(sut.OwnsThread(new ThreadCommentRef("18", "200", TokenIdentityId, null)));
    }

    [Fact]
    public void ContributeIdentity_ResolvedNothing_LeavesTheIdentityThePassAlreadyHad()
    {
        // A handshake that could not name the account has nothing to say, and must not erase what the pass
        // was built with.
        var sut = ThreadOwnershipResolver.Create(
            [],
            new ThreadOwnerIdentity(TokenIdentityId),
            ProviderCommentIdScope.Thread);

        sut.ContributeIdentity(ThreadOwnerIdentity.None);

        Assert.True(sut.OwnsThread(new ThreadCommentRef("17", "100", TokenIdentityId, null)));
    }

    [Fact]
    public void None_IsNotShared_SoOnePassCannotContributeAnIdentityIntoAnother()
    {
        // The degraded resolver is handed out wherever no provenance store is reachable, on every pass of
        // every client. One shared instance would carry the account one client posts as into the next.
        ThreadOwnershipResolver.None.ContributeIdentity(new ThreadOwnerIdentity(TokenIdentityId));

        Assert.False(ThreadOwnershipResolver.None.OwnsThread(new ThreadCommentRef("17", "100", TokenIdentityId, null)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OneThread_AskedAsAThreadAndAsAComment_GetsTheSameAnswer(bool identifiedByGuid)
    {
        // The review side asks about a thread and the crawl side asks about its first comment. They are the
        // same question, so one thread cannot come back ProPR's to one caller and human to the other. Holds
        // for an identity-GUID provider and a login provider alike.
        var identity = identifiedByGuid
            ? new ThreadOwnerIdentity(TokenIdentityId)
            : new ThreadOwnerIdentity(Login: "meister-dev");
        var first = identifiedByGuid
            ? new ThreadCommentRef("17", "100", TokenIdentityId)
            : new ThreadCommentRef("17", "100", AuthorLogin: "meister-dev");
        var human = identifiedByGuid
            ? new ThreadCommentRef("18", "200", HumanAuthorId)
            : new ThreadCommentRef("18", "200", AuthorLogin: "jane");

        var sut = ThreadOwnershipResolver.Create(
            [],
            identity,
            identifiedByGuid ? ProviderCommentIdScope.Thread : ProviderCommentIdScope.PullRequest);

        Assert.Equal(sut.OwnsThread(first), sut.OwnsComment(first));
        Assert.True(sut.OwnsThread(first));
        Assert.Equal(sut.OwnsThread(human), sut.OwnsComment(human));
        Assert.False(sut.OwnsThread(human));
    }

    [Fact]
    public void None_OwnsNothing()
    {
        Assert.False(ThreadOwnershipResolver.None.OwnsThread(new ThreadCommentRef("17", "100", HumanAuthorId, "jane")));
        Assert.False(ThreadOwnershipResolver.None.OwnsComment(new ThreadCommentRef("17", "100", null, null)));
    }

    [Fact]
    public void Owns_AnAbsentAuthorAndAnAbsentIdentity_AreNotTreatedAsEqual()
    {
        var sut = ThreadOwnershipResolver.Create([], ThreadOwnerIdentity.None, ProviderCommentIdScope.Thread);

        Assert.False(sut.OwnsComment(new ThreadCommentRef(null, null, null, null)));
    }

    [Theory]
    [InlineData(ScmProvider.AzureDevOps, ProviderCommentIdScope.Thread)]
    [InlineData(ScmProvider.GitHub, ProviderCommentIdScope.PullRequest)]
    [InlineData(ScmProvider.GitLab, ProviderCommentIdScope.PullRequest)]
    [InlineData(ScmProvider.Forgejo, ProviderCommentIdScope.PullRequest)]
    public void CommentIdScope_IsTheOneStatedPerProvider(ScmProvider provider, ProviderCommentIdScope expected)
    {
        Assert.Equal(expected, ProviderCommentIdScopes.For(provider));
    }
}
