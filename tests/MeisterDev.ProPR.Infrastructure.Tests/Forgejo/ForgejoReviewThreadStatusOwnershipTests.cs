// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using MeisterDev.ProPR.Application.Features.ReviewArchive;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Security;

namespace MeisterDev.ProPR.Infrastructure.Tests.Forgejo;

/// <summary>
///     Which Forgejo review anchors are reported as ProPR's: provenance first, then the login the
///     connection's token authenticates as, and nothing else.
/// </summary>
public sealed class ForgejoReviewThreadStatusOwnershipTests
{
    private const string AuthenticatedLogin = "meister-dev";

    [Fact]
    public async Task GetReviewerThreadStatusesAsync_ProvenanceRecordsTheFirstComment_IncludesTheAnchor()
    {
        // Forgejo comment ids are unique within the pull request, so provenance resolves on the comment id
        // even though what was recorded as the thread id is the review id the anchor grouping never carries.
        var sut = BuildSut(out var clientId, "retired-service-account", "jane");

        var result = await sut.GetReviewerThreadStatusesAsync(
            "https://codeberg.example.com",
            "acme",
            "acme/propr",
            42,
            ThreadOwnershipResolver.Create(
                [new PostedCommentOriginRow("7001", "501", Guid.NewGuid())],
                ThreadOwnerIdentity.None,
                ProviderCommentIdScope.PullRequest),
            clientId,
            CancellationToken.None);

        var entry = Assert.Single(result);

        // Forgejo has no thread object, so the anchor carries no identifier rather than a comment's.
        Assert.Null(entry.ThreadId);
        Assert.Equal(1, entry.NonReviewerReplyCount);
    }

    [Fact]
    public async Task GetReviewerThreadStatusesAsync_NoProvenanceButFirstCommentIsTheAuthenticatedUser_IncludesTheAnchor()
    {
        var sut = BuildSut(out var clientId, AuthenticatedLogin, "jane");

        var result = await sut.GetReviewerThreadStatusesAsync(
            "https://codeberg.example.com",
            "acme",
            "acme/propr",
            42,
            ThreadOwnershipResolver.None,
            clientId,
            CancellationToken.None);

        var entry = Assert.Single(result);

        // Forgejo has no thread object, so the anchor carries no identifier rather than a comment's.
        Assert.Null(entry.ThreadId);
        Assert.Equal(1, entry.NonReviewerReplyCount);
    }

    [Fact]
    public async Task GetReviewerThreadStatusesAsync_NoProvenanceAndAHumanRaisedIt_ExcludesTheAnchor()
    {
        var sut = BuildSut(out var clientId, "jane", AuthenticatedLogin);

        var result = await sut.GetReviewerThreadStatusesAsync(
            "https://codeberg.example.com",
            "acme",
            "acme/propr",
            42,
            ThreadOwnershipResolver.None,
            clientId,
            CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetReviewerThreadStatusesAsync_AnchorRaisedByTheConfiguredReviewer_IsExcluded()
    {
        // The deliberate narrowing. A client can configure one account as its review trigger and connect
        // with another; only the account the token authenticates as owns threads now, so an anchor the
        // configured reviewer raised with nothing recorded against it stays out.
        var sut = BuildSut(out var clientId, "configured-review-bot", "jane");

        var result = await sut.GetReviewerThreadStatusesAsync(
            "https://codeberg.example.com",
            "acme",
            "acme/propr",
            42,
            ThreadOwnershipResolver.None,
            clientId,
            CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetReviewerThreadStatusesAsync_ReplyRecordedAsProPRs_IsNotCountedAsANonReviewerReply()
    {
        // The reply arrived under an account the connection does not recognise, but ProPR recorded posting
        // it, so it is not a reply waiting for an answer.
        var sut = BuildSut(out var clientId, AuthenticatedLogin, "retired-service-account");

        var result = await sut.GetReviewerThreadStatusesAsync(
            "https://codeberg.example.com",
            "acme",
            "acme/propr",
            42,
            ThreadOwnershipResolver.Create(
                [new PostedCommentOriginRow("7002", "502", Guid.NewGuid())],
                ThreadOwnerIdentity.None,
                ProviderCommentIdScope.PullRequest),
            clientId,
            CancellationToken.None);

        Assert.Equal(0, Assert.Single(result).NonReviewerReplyCount);
    }

    private static ForgejoReviewThreadStatusProvider BuildSut(
        out Guid clientId,
        string firstAuthor,
        string replyAuthor)
    {
        clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var connectionRepository = ForgejoTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = ForgejoTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://codeberg.example.com/api/v1/user" =>
                    ForgejoTestHelpers.CreateJsonResponse(new { login = AuthenticatedLogin }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews?limit=100" =>
                    ForgejoTestHelpers.CreateJsonResponse(
                        new object[]
                        {
                            new { id = 7001, state = "COMMENT", user = new { id = 99, login = firstAuthor } },
                            new { id = 7002, state = "COMMENT", user = new { id = 7, login = replyAuthor } },
                        }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews/7001/comments" =>
                    ForgejoTestHelpers.CreateJsonResponse(
                        new object[]
                        {
                            new
                            {
                                id = 501, body = "Please handle null.", path = "src/feature.ts", position = 18,
                                user = new { id = 99, login = firstAuthor },
                                created_at = "2026-04-14T08:00:00Z",
                            },
                        }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews/7002/comments" =>
                    ForgejoTestHelpers.CreateJsonResponse(
                        new object[]
                        {
                            new
                            {
                                id = 502, body = "Done.", path = "src/feature.ts", position = 18,
                                user = new { id = 7, login = replyAuthor },
                                created_at = "2026-04-14T08:01:00Z",
                            },
                        }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        return new ForgejoReviewThreadStatusProvider(
            new ForgejoConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);
    }
}
