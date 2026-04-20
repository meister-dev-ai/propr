// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Domain.Tests.ValueObjects;

public sealed class ProviderReferenceTests
{
    [Fact]
    public void ProviderHostRef_TrimsAndNormalizesHostBaseUrl()
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, " https://github.com/ ");

        Assert.Equal(ScmProvider.GitHub, host.Provider);
        Assert.Equal("https://github.com", host.HostBaseUrl);
    }

    [Fact]
    public void ProviderHostRef_ThrowsForInvalidAbsoluteUrl()
    {
        Assert.Throws<ArgumentException>(() => new ProviderHostRef(ScmProvider.GitHub, "not-a-url"));
    }

    [Fact]
    public void RepositoryRef_RequiresStableRepositoryIdentity()
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");

        Assert.Throws<ArgumentException>(() => new RepositoryRef(host, " ", "acme", "acme/propr"));
    }

    [Fact]
    public void CodeReviewRef_RequiresPositiveReviewNumber()
    {
        var repository = new RepositoryRef(
            new ProviderHostRef(ScmProvider.GitHub, "https://github.com"),
            "repo-gh-1",
            "acme",
            "acme/propr");

        Assert.Throws<ArgumentOutOfRangeException>(() => new CodeReviewRef(
            repository,
            CodeReviewPlatformKind.PullRequest,
            "42",
            0));
    }

    [Fact]
    public void ReviewRevision_TrimsStableIdentifiers()
    {
        var revision = new ReviewRevision(" head-sha ", " base-sha ", " start-sha ", " revision-1 ", " patch-1 ");

        Assert.Equal("head-sha", revision.HeadSha);
        Assert.Equal("base-sha", revision.BaseSha);
        Assert.Equal("start-sha", revision.StartSha);
        Assert.Equal("revision-1", revision.ProviderRevisionId);
        Assert.Equal("patch-1", revision.PatchIdentity);
    }

    [Fact]
    public void ReviewerIdentity_RequiresStableUserIdentityAndLogin()
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");

        Assert.Throws<ArgumentException>(() => new ReviewerIdentity(host, " ", "meister-bot", "Meister Bot", true));
        Assert.Throws<ArgumentException>(() => new ReviewerIdentity(host, "user-1", " ", "Meister Bot", true));
    }

    [Fact]
    public void ReviewThreadRef_AllowsOptionalLocationMetadata()
    {
        var review = CreateReview();

        var thread = new ReviewThreadRef(review, "thread-1", null, null, true);

        Assert.Equal(review, thread.Review);
        Assert.Null(thread.FilePath);
        Assert.Null(thread.LineNumber);
        Assert.True(thread.IsReviewerOwned);
    }

    [Fact]
    public void ReviewCommentRef_PreservesAuthorContext()
    {
        var author = new ReviewerIdentity(
            new ProviderHostRef(ScmProvider.GitHub, "https://github.com"),
            "user-1",
            "meister-bot",
            "Meister Bot",
            true);

        var comment = new ReviewCommentRef(
            new ReviewThreadRef(CreateReview(), "thread-1", "src/file.cs", 18, true),
            "comment-1",
            author,
            DateTimeOffset.Parse("2026-04-08T09:00:00Z"));

        Assert.Equal("comment-1", comment.ExternalCommentId);
        Assert.Equal(author, comment.Author);
    }

    [Fact]
    public void WebhookDeliveryEnvelope_CanCarryProviderNeutralReviewContext()
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "repo-gh-1", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", "start-sha", "revision-1", "patch-1");
        var actor = new ReviewerIdentity(host, "user-1", "meister-bot", "Meister Bot", true);

        var envelope = new WebhookDeliveryEnvelope(
            host,
            "delivery-1",
            "pull_request",
            "review.updated",
            repository,
            review,
            revision,
            "refs/heads/feature/provider-neutral",
            "refs/heads/main",
            actor);

        Assert.Equal(host, envelope.Host);
        Assert.Equal(review, envelope.Review);
        Assert.Equal(revision, envelope.Revision);
        Assert.Equal(actor, envelope.Actor);
    }

    private static CodeReviewRef CreateReview()
    {
        var repository = new RepositoryRef(
            new ProviderHostRef(ScmProvider.GitHub, "https://github.com"),
            "repo-gh-1",
            "acme",
            "acme/propr");

        return new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
    }
}
