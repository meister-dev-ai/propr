// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.GitHub;

/// <summary>
///     Placement of findings against the diff GitHub resolves review comments on. GitHub refuses the whole review
///     with 422 "Line could not be resolved" when one inline comment names a line outside the diff of the
///     reviewed commit.
/// </summary>
public sealed class GitHubReviewLinePlacementTests
{
    private const string FilePath = "review-line-check.txt";
    private const string UserUri = "https://api.github.com/user";
    private const string CompareUri = "https://api.github.com/repos/acme/propr/compare/base-sha...head-sha?per_page=1";
    private const string ReviewsUri = "https://api.github.com/repos/acme/propr/pulls/42/reviews";
    private const string ReviewCommentsUri = "https://api.github.com/repos/acme/propr/pulls/42/comments?per_page=100";

    private static readonly ProviderHostRef Host = new(ScmProvider.GitHub, "https://github.com");
    private static readonly CodeReviewRef Review = new(new RepositoryRef(Host, "101", "acme", "acme/propr"), CodeReviewPlatformKind.PullRequest, "42", 42);
    private static readonly ReviewRevision Revision = new("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha");
    private static readonly ReviewerIdentity Reviewer = new(Host, "99", "meister-review-bot[bot]", "Meister Review Bot", true);

    [Fact]
    public async Task PublishReviewAsync_ListsAFindingOutsideTheDiffInTheSummary()
    {
        var result = new ReviewResult(
            "Looks solid overall.",
            [
                new ReviewComment(FilePath, 10, CommentSeverity.Error, "The unchanged loop reads past the end."),
                new ReviewComment(FilePath, 30, CommentSeverity.Warning, "The changed line drops the suffix."),
            ]);
        var server = new GitHubStub(compare: Comparison(), reviewComments: [new { id = 9001L, path = FilePath, line = 30, pull_request_review_id = 555L }]);

        var diagnostics = await server.PublishAsync(result);

        var review = Assert.Single(server.PostedReviews);
        var inline = Assert.Single(review.GetProperty("comments").EnumerateArray());
        Assert.Equal(30, inline.GetProperty("line").GetInt32());
        Assert.Contains("The changed line drops the suffix.", inline.GetProperty("body").GetString());
        var summary = review.GetProperty("body").GetString()!;
        Assert.Contains("review-line-check.txt:10", summary);
        Assert.Contains("The unchanged loop reads past the end.", summary);
        Assert.Contains("1 finding refers to a line outside the diff of this pull request.", summary);
        Assert.Equal(2, diagnostics.PostedCount);
        Assert.Equal(1, diagnostics.PlacedInSummaryCount);

        // The created comment belongs to the second finding, although only one comment was sent.
        var posted = Assert.Single(diagnostics.PostedComments);
        Assert.Equal(1, posted.FindingOrdinal);
        Assert.Equal(30, posted.Line);
    }

    [Fact]
    public async Task PublishReviewAsync_KeepsAReplacementRangeAcrossTwoHunksApplicable()
    {
        var result = new ReviewResult("Narrative.", [FindingWithFix(43, 32, 43)]);
        var server = new GitHubStub(compare: Comparison());

        var diagnostics = await server.PublishAsync(result, currentHeadSha: "head-sha");

        var inline = Assert.Single(Assert.Single(server.PostedReviews).GetProperty("comments").EnumerateArray());
        Assert.Equal(43, inline.GetProperty("line").GetInt32());
        Assert.Equal(32, inline.GetProperty("start_line").GetInt32());
        Assert.Contains("```suggestion", inline.GetProperty("body").GetString());
        Assert.Equal(0, diagnostics.PlacedInSummaryCount);
    }

    [Fact]
    public async Task PublishReviewAsync_ShowsAReplacementRangeStartingOutsideTheDiffAsOrdinaryCode()
    {
        var result = new ReviewResult("Narrative.", [FindingWithFix(30, 20, 30)]);
        var server = new GitHubStub(compare: Comparison());

        await server.PublishAsync(result, currentHeadSha: "head-sha");

        var inline = Assert.Single(Assert.Single(server.PostedReviews).GetProperty("comments").EnumerateArray());
        Assert.Equal(30, inline.GetProperty("line").GetInt32());
        Assert.False(inline.TryGetProperty("start_line", out _));
        var body = inline.GetProperty("body").GetString()!;
        Assert.DoesNotContain("```suggestion", body);
        Assert.Contains("**Suggested change**", body);
    }

    [Fact]
    public async Task PublishReviewAsync_WithoutTheComparison_SendsEveryFindingInline()
    {
        var result = new ReviewResult(
            "Narrative.",
            [
                new ReviewComment(FilePath, 10, CommentSeverity.Error, "Outside."),
                new ReviewComment(FilePath, 30, CommentSeverity.Warning, "Inside."),
            ]);
        var server = new GitHubStub(compare: null);

        await server.PublishAsync(result);

        var lines = Assert.Single(server.PostedReviews).GetProperty("comments").EnumerateArray()
            .Select(comment => comment.GetProperty("line").GetInt32());
        Assert.Equal([10, 30], lines);
    }

    [Fact]
    public async Task PublishReviewAsync_WhenGitHubRefusesALine_SendsTheReviewAgainWithEveryFindingInTheSummary()
    {
        var result = new ReviewResult(
            "Narrative.",
            [
                new ReviewComment(FilePath, 10, CommentSeverity.Error, "The unchanged loop reads past the end."),
                new ReviewComment(FilePath, 30, CommentSeverity.Warning, "The changed line drops the suffix."),
                new ReviewComment(null, null, CommentSeverity.Info, "The description is accurate."),
            ]);
        var server = new GitHubStub(compare: null, refusedReviews: 1);

        var diagnostics = await server.PublishAsync(result);

        Assert.Equal(2, server.PostedReviews.Count);
        Assert.Equal(2, server.PostedReviews[0].GetProperty("comments").GetArrayLength());
        var retry = server.PostedReviews[1];
        Assert.Equal(0, retry.GetProperty("comments").GetArrayLength());
        var summary = retry.GetProperty("body").GetString()!;
        Assert.Contains("The unchanged loop reads past the end.", summary);
        Assert.Contains("The changed line drops the suffix.", summary);
        Assert.Contains("The description is accurate.", summary);
        Assert.Contains("2 findings refer to lines outside the diff of this pull request.", summary);
        Assert.Equal(3, diagnostics.PostedCount);
        Assert.Equal(2, diagnostics.PlacedInSummaryCount);
        Assert.Empty(diagnostics.PostedComments);

        // The summary-only review created no comment, so the pull request's comment listing is not read.
        Assert.DoesNotContain(ReviewCommentsUri, server.RequestedUris);
    }

    [Fact]
    public async Task PublishReviewAsync_WhenGitHubAlsoRefusesTheSummaryOnlyReview_Throws()
    {
        var result = new ReviewResult("Narrative.", [new ReviewComment(FilePath, 30, CommentSeverity.Warning, "Inside.")]);
        var server = new GitHubStub(compare: null, refusedReviews: 2);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => server.PublishAsync(result));

        Assert.Contains("status 422", exception.Message);
        Assert.Equal(2, server.PostedReviews.Count);
    }

    [Fact]
    public async Task PublishReviewAsync_WhenGitHubRefusesAReviewWithoutInlineComments_DoesNotSendItAgain()
    {
        var result = new ReviewResult("Narrative.", [new ReviewComment(null, null, CommentSeverity.Info, "General.")]);
        var server = new GitHubStub(compare: null, refusedReviews: 2);

        await Assert.ThrowsAsync<InvalidOperationException>(() => server.PublishAsync(result));

        Assert.Single(server.PostedReviews);
    }

    [Fact]
    public async Task PublishReviewAsync_MatchesACommentShiftedByAPushThroughItsOriginalLine()
    {
        var result = new ReviewResult("Narrative.", [new ReviewComment(FilePath, 30, CommentSeverity.Warning, "The changed line drops the suffix.")]);
        var server = new GitHubStub(
            compare: Comparison(),
            reviewComments: [new { id = 9001L, path = FilePath, line = 34, original_line = 30, pull_request_review_id = 555L }]);

        var diagnostics = await server.PublishAsync(result);

        var posted = Assert.Single(diagnostics.PostedComments);
        Assert.Equal(0, posted.FindingOrdinal);
        Assert.Equal(30, posted.Line);
    }

    [Fact]
    public async Task PublishReviewAsync_TellsApartTwoCommentsOnTheSameLineByTheirBody()
    {
        var result = new ReviewResult(
            "Narrative.",
            [
                new ReviewComment(FilePath, 30, CommentSeverity.Warning, "The changed line drops the suffix."),
                new ReviewComment(FilePath, 30, CommentSeverity.Error, "The changed line breaks the parser."),
            ]);
        var server = new GitHubStub(compare: Comparison());
        await server.PublishAsync(result);
        var bodies = Assert.Single(server.PostedReviews).GetProperty("comments").EnumerateArray()
            .Select(comment => comment.GetProperty("body").GetString()!).ToArray();
        var withComments = new GitHubStub(
            compare: Comparison(),
            reviewComments:
            [
                new { id = 9002L, path = FilePath, line = 30, body = bodies[1], pull_request_review_id = 555L },
                new { id = 9001L, path = FilePath, line = 30, body = bodies[0], pull_request_review_id = 555L },
            ]);

        var diagnostics = await withComments.PublishAsync(result);

        Assert.Equal(1, Assert.Single(diagnostics.PostedComments, comment => comment.ProviderCommentId == "9002").FindingOrdinal);
        Assert.Equal(0, Assert.Single(diagnostics.PostedComments, comment => comment.ProviderCommentId == "9001").FindingOrdinal);
    }

    [Fact]
    public async Task PublishReviewAsync_AssignsIdenticalCommentsOnTheSameLineInCreationOrder()
    {
        var duplicate = new ReviewComment(FilePath, 30, CommentSeverity.Warning, "The changed line drops the suffix.");
        var result = new ReviewResult("Narrative.", [duplicate, duplicate with { }]);
        var server = new GitHubStub(compare: Comparison());
        await server.PublishAsync(result);
        var body = Assert.Single(server.PostedReviews).GetProperty("comments").EnumerateArray().First().GetProperty("body").GetString()!;
        var withComments = new GitHubStub(
            compare: Comparison(),
            reviewComments:
            [
                new { id = 9002L, path = FilePath, line = 30, body, pull_request_review_id = 555L },
                new { id = 9001L, path = FilePath, line = 30, body, pull_request_review_id = 555L },
            ]);

        var diagnostics = await withComments.PublishAsync(result);

        Assert.Equal(["9001", "9002"], diagnostics.PostedComments.Select(comment => comment.ProviderCommentId));
        Assert.Equal([0, 1], diagnostics.PostedComments.Select(comment => comment.FindingOrdinal ?? -1));
    }

    private static object Comparison()
    {
        return new { files = new[] { new { filename = FilePath, status = "modified", patch = GitHubDiffLinesTests.TwoHunkPatch } } };
    }

    private static ReviewComment FindingWithFix(int line, int startLine, int endLine)
    {
        var original = string.Join('\n', Enumerable.Range(startLine, endLine - startLine + 1).Select(number => $"line {number}"));
        return new ReviewComment(FilePath, line, CommentSeverity.Warning, "The range repeats a value.")
        {
            SuggestedFix = new ReviewSuggestedFix(
                startLine, endLine, original, "replacement();", "head-sha", FilePath, IsSourceValidated: true, SourceAnchorLineNumber: line),
        };
    }

    private sealed class GitHubStub(object? compare, IReadOnlyList<object>? reviewComments = null, int refusedReviews = 0)
    {
        private int _refusalsLeft = refusedReviews;

        public List<JsonElement> PostedReviews { get; } = [];

        public List<string> RequestedUris { get; } = [];

        public async Task<ReviewCommentPostingDiagnosticsDto> PublishAsync(ReviewResult result, string? currentHeadSha = null)
        {
            var clientId = Guid.NewGuid();
            var connections = Substitute.For<IClientScmConnectionRepository>();
            connections.GetOperationalConnectionAsync(clientId, Host, Arg.Any<CancellationToken>())
                .Returns(
                    new ClientScmConnectionCredentialDto(
                        Guid.NewGuid(), clientId, ScmProvider.GitHub, Host.HostBaseUrl, ScmAuthenticationKind.PersonalAccessToken, "GitHub", "ghp_test", true));

            var httpClientFactory = Substitute.For<IHttpClientFactory>();
            using var httpClient = new HttpClient(new Handler(this.RespondAsync));
            httpClientFactory.CreateClient("GitHubProvider").Returns(httpClient);

            var sut = new GitHubCodeReviewPublicationService(
                new GitHubConnectionVerifier(connections, httpClientFactory), httpClientFactory, TestPostedCommentComposer.Default);
            return await sut.PublishReviewAsync(
                clientId, Review, Revision, result, Reviewer,
                publicationContext: new ReviewPublicationContext(Review, Revision, Reviewer, []) { CurrentHeadSha = currentHeadSha });
        }

        private async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request)
        {
            this.RequestedUris.Add(request.RequestUri!.AbsoluteUri);
            switch (request.RequestUri!.AbsoluteUri)
            {
                case UserUri:
                    return Json(new { login = "meister-dev" });
                case CompareUri when compare is not null:
                    return Json(compare);
                case ReviewsUri:
                    using (var posted = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()))
                    {
                        this.PostedReviews.Add(posted.RootElement.Clone());
                    }

                    if (this._refusalsLeft > 0)
                    {
                        this._refusalsLeft--;
                        return Json(
                            new { message = "Unprocessable Entity", errors = new[] { "Line could not be resolved" }, status = "422" },
                            HttpStatusCode.UnprocessableEntity);
                    }

                    return Json(new { id = 555L });
                case ReviewCommentsUri:
                    return Json(reviewComments ?? []);
                default:
                    return Json(new { message = "Not Found" }, HttpStatusCode.NotFound);
            }
        }

        private static HttpResponseMessage Json(object payload, HttpStatusCode status = HttpStatusCode.OK)
        {
            return new HttpResponseMessage(status) { Content = new StringContent(JsonSerializer.Serialize(payload)) };
        }

        private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                return responder(request);
            }
        }
    }
}
