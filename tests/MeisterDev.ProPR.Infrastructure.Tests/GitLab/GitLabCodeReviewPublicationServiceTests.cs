// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using MeisterDev.ProPR.Application.Exceptions;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Security;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.GitLab;

public sealed class GitLabPublicationContextContractTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(FindingBadgeStyle.ShieldsIo)]
    [InlineData(FindingBadgeStyle.Plain)]
    public async Task PublishReviewAsync_NeutralizesMarkupAndPreservesCodeBeforeAddingAssessmentWrappers(FindingBadgeStyle? style)
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var repository = new RepositoryRef(host, "101", "acme/platform", "acme/platform/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", "start-sha", "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "bot", "Bot </details><script>author</script>", true);
        var finding = new ReviewComment("src/</details><script>action</script>.cs", 18, CommentSeverity.Warning, "Guard <b>the service</b>. </details>")
        {
            RiskAssessment = new FindingRiskAssessment(
                FindingImpact.Moderate, FindingLikelihood.Possible,
                "Rejects `count < 0 && value > 1` and `a\\|b`. </details><script>impact</script>",
                "On retry.\n</details><script>likelihood</script>", "review_model", FindingRiskAssessment.CurrentRubricVersion,
                FindingConfidence.Medium, "Known caller. </details><script>confidence</script>"),
        };
        var result = new ReviewResult(
            "Conclusion </details><script>conclusion</script>.", [finding, finding.AsPullRequestLevel("Overview </details><script>fileless</script>.")])
        {
            BudgetSoftCapped = true,
            BudgetSoftCapSkippedFilePaths = ["src/</details><script>budget</script>.cs"],
            ContextDegradedFilePaths = ["src/</details><script>degraded</script>.cs"],
            ContextSkippedFilePaths = ["src/</details><script>skipped</script>.cs"],
        };
        var postedBodies = new List<string>();
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(async request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri.EndsWith("/discussions", StringComparison.Ordinal))
            {
                postedBodies.Add(GitLabTestHelpers.ReadMultipartField(await request.Content!.ReadAsStringAsync(), "body"));
                return GitLabTestHelpers.CreateJsonResponse(new { id = "discussion-1" }, HttpStatusCode.Created);
            }

            return uri.EndsWith("/user", StringComparison.Ordinal)
                ? GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" })
                : GitLabTestHelpers.CreateJsonResponse(
                    new[] { new { id = 7L, base_commit_sha = "base-sha", head_commit_sha = "head-sha", start_commit_sha = "start-sha" } });
        });
        var composer = TestPostedCommentComposer.With("Generated </details><script>marker</script>.");
        var sut = new GitLabCodeReviewPublicationService(new GitLabConnectionVerifier(connectionRepository, httpClientFactory), httpClientFactory, composer);

        await sut.PublishReviewAsync(
            clientId, review, revision, result, reviewer,
            publicationContext: style.HasValue ? new ReviewPublicationContext(review, revision, reviewer, []) { FindingBadgeStyle = style.Value } : null);

        Assert.Equal(2, postedBodies.Count);
        var prefix = style == FindingBadgeStyle.Plain ? "`Severity: Warning`" : "![Severity: Warning]";
        Assert.All(postedBodies, body => Assert.Contains(prefix, body));
        foreach (var body in postedBodies)
        {
            Assert.Contains("<details>\n<summary>Assessment details</summary>\n\n| Assessment | Level | Reason |", body);
            Assert.Contains("`count &lt; 0 &amp;&amp; value &gt; 1`", body);
            Assert.Contains("`a\\\\|b`", body);
            Assert.Contains("&lt;/details&gt;&lt;script&gt;impact&lt;/script&gt;", body);
            Assert.DoesNotContain("&amp;lt;", body);
            Assert.DoesNotContain("<script>", Markdig.Markdown.ToHtml(body));
            Assert.Equal(1, body.Split("<details>", StringSplitOptions.None).Length - 1);
            Assert.Equal(1, body.Split("</details>", StringSplitOptions.None).Length - 1);
            TestPostedCommentComposer.AssertMarkedOnce(body, "*Generated &lt;/details&gt;&lt;script&gt;marker&lt;/script&gt;.*");
        }

        var summary = postedBodies[0];
        foreach (var field in new[] { "author", "conclusion", "action", "budget", "degraded", "skipped" })
        {
            Assert.Contains($"&lt;/details&gt;&lt;script&gt;{field}&lt;/script&gt;", summary);
        }

        Assert.Contains("<\u200B/details><\u200Bscript>fileless<\u200B/script>", summary);
        Assert.Contains("| 2 | Medium: 2 | Medium: 2 |", summary);
        Assert.Equal(1, summary.Split("Overview ", StringSplitOptions.None).Length - 1);
        Assert.StartsWith(prefix, postedBodies[1]);
        Assert.Contains("\n\nGuard <\u200Bb>the service<\u200B/b>.", postedBodies[1]);
        Assert.Equal("Guard <b>the service</b>. </details>", finding.Message);
    }

    [Fact]
    public void ProviderAdapters_RegisterGitLabPublicationUnderNeutralInterface()
    {
        var services = new ServiceCollection();

        services.AddSingleton(Substitute.For<IClientScmConnectionRepository>());
        services.AddGitLabProviderAdapters();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var publicationService = scope.ServiceProvider
            .GetServices<ICodeReviewPublicationService>()
            .Single(service => service.Provider == ScmProvider.GitLab);

        Assert.IsType<GitLabCodeReviewPublicationService>(publicationService);
    }

    [Fact]
    public async Task PublishReviewAsync_AcceptsPublicationContextWithoutChangingResultAccounting()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var repository = new RepositoryRef(host, "101", "acme/platform", "acme/platform/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", "start-sha", "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true);
        var result = new ReviewResult("Looks solid overall.", []);
        var publicationContext = new ReviewPublicationContext(
            review,
            revision,
            reviewer,
            [new PrCommentThread("1", null, null, [new PrThreadComment("Bot", "Summary")])]);
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://gitlab.example.com/api/v4/user" => GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" }),
                "https://gitlab.example.com/api/v4/projects/101/merge_requests/42/discussions" =>
                    GitLabTestHelpers.CreateJsonResponse(new { id = "discussion-1" }, HttpStatusCode.Created),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new GitLabCodeReviewPublicationService(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory,
            TestPostedCommentComposer.Distinctive);

        var diagnostics = await sut.PublishReviewAsync(clientId, review, revision, result, reviewer, publicationContext: publicationContext);

        Assert.Equal(0, diagnostics.PostedCount);
        Assert.Equal(0, diagnostics.SuppressedCount);
        Assert.Equal(0, diagnostics.CandidateCount);
    }

    [Fact]
    public async Task PublishReviewAsync_CapturesCreatedDiscussionNoteIds()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var repository = new RepositoryRef(host, "101", "acme/platform", "acme/platform/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", "start-sha", "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true);
        var result = new ReviewResult(
            "Looks solid overall.",
            [new ReviewComment("src/file.ts", 18, CommentSeverity.Warning, "Guard this null case.")]);

        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://gitlab.example.com/api/v4/user" => GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" }),
                "https://gitlab.example.com/api/v4/projects/101/merge_requests/42/versions" =>
                    GitLabTestHelpers.CreateJsonResponse(
                        new[]
                        {
                            new { id = 7L, base_commit_sha = "base-sha", head_commit_sha = "head-sha", start_commit_sha = "start-sha" },
                        }),
                "https://gitlab.example.com/api/v4/projects/101/merge_requests/42/discussions" =>
                    GitLabTestHelpers.CreateJsonResponse(
                        new { id = "discussion-abc", notes = new[] { new { id = 9100L } } },
                        HttpStatusCode.Created),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new GitLabCodeReviewPublicationService(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory,
            TestPostedCommentComposer.Distinctive);

        var diagnostics = await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        // The summary discussion and the inline discussion both report their first created note id.
        Assert.All(diagnostics.PostedComments, reference => Assert.Equal("9100", reference.ProviderCommentId));
        Assert.All(diagnostics.PostedComments, reference => Assert.Equal("discussion-abc", reference.ProviderThreadId));
        Assert.Contains(diagnostics.PostedComments, reference => reference.FilePath == "src/file.ts" && reference.Line == 18);
        Assert.Equal(2, diagnostics.PostedComments.Count);
    }

    [Fact]
    public async Task PublishReviewAsync_WhenDiscussionResponseHasNoNoteIds_PublishesWithEmptyPostedComments()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var repository = new RepositoryRef(host, "101", "acme/platform", "acme/platform/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", "start-sha", "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true);
        var result = new ReviewResult("Looks solid overall.", []);

        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://gitlab.example.com/api/v4/user" => GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" }),
                "https://gitlab.example.com/api/v4/projects/101/merge_requests/42/discussions" =>
                    GitLabTestHelpers.CreateJsonResponse(new { id = "discussion-abc" }, HttpStatusCode.Created),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new GitLabCodeReviewPublicationService(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory,
            TestPostedCommentComposer.Distinctive);

        var diagnostics = await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        Assert.Empty(diagnostics.PostedComments);
        Assert.Equal(0, diagnostics.PostedCount);
    }

    [Fact]
    public async Task PublishReviewAsync_OneInlineDiscussionRejected_StillPostsSummaryAndOtherInline()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var repository = new RepositoryRef(host, "101", "acme/platform", "acme/platform/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", "start-sha", "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true);
        var result = new ReviewResult(
            "Looks solid overall.",
            [
                new ReviewComment("src/a.ts", 10, CommentSeverity.Warning, "first inline"),
                new ReviewComment("src/b.ts", 20, CommentSeverity.Error, "second inline"),
            ]);

        // Discussion POST order: #1 summary, #2 first inline, #3 second inline. Reject only the first inline.
        var discussionPosts = 0;
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri.EndsWith("/user", StringComparison.Ordinal))
            {
                return GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" });
            }

            if (uri.EndsWith("/versions", StringComparison.Ordinal))
            {
                return GitLabTestHelpers.CreateJsonResponse(
                    new[]
                    {
                        new { id = 7L, base_commit_sha = "base-sha", head_commit_sha = "head-sha", start_commit_sha = "start-sha" },
                    });
            }

            if (uri.EndsWith("/discussions", StringComparison.Ordinal))
            {
                discussionPosts++;
                return discussionPosts == 2
                    ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("invalid line for position") }
                    : GitLabTestHelpers.CreateJsonResponse(
                        new { id = $"discussion-{discussionPosts}", notes = new[] { new { id = 9100L } } },
                        HttpStatusCode.Created);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new GitLabCodeReviewPublicationService(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory,
            TestPostedCommentComposer.Distinctive);

        var diagnostics = await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        // Summary + the second inline still post even though the first inline was rejected.
        Assert.Equal(3, discussionPosts);
        Assert.Equal(1, diagnostics.PostedCount);
        Assert.Equal(1, diagnostics.FailedCount);
        var failure = Assert.Single(diagnostics.PostingFailures);
        Assert.Equal("inline", failure.ThreadKind);
        Assert.Equal("src/a.ts", failure.FilePath);
        Assert.Equal(10, failure.Line);
    }

    [Fact]
    public async Task PublishReviewAsync_MixedSameAnchorResultsRetainTheOriginalFindingOrdinal()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var repository = new RepositoryRef(host, "101", "acme/platform", "acme/platform/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", "start-sha", "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true);
        var result = new ReviewResult(
            "Looks solid overall.",
            [
                new ReviewComment("src/a.ts", 10, CommentSeverity.Warning, "first inline"),
                new ReviewComment(null, null, CommentSeverity.Warning, "Overview concern"),
                new ReviewComment("src/a.ts", 10, CommentSeverity.Error, "second inline"),
            ]);

        // Discussion POST order: #1 summary, #2 first inline, #3 second inline. Reject only the first inline.
        var discussionPosts = 0;
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri.EndsWith("/user", StringComparison.Ordinal))
            {
                return GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" });
            }

            if (uri.EndsWith("/versions", StringComparison.Ordinal))
            {
                return GitLabTestHelpers.CreateJsonResponse(
                    new[]
                    {
                        new { id = 7L, base_commit_sha = "base-sha", head_commit_sha = "head-sha", start_commit_sha = "start-sha" },
                    });
            }

            if (uri.EndsWith("/discussions", StringComparison.Ordinal))
            {
                discussionPosts++;
                return discussionPosts == 2
                    ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("invalid line for position") }
                    : GitLabTestHelpers.CreateJsonResponse(
                        new { id = $"discussion-{discussionPosts}", notes = new[] { new { id = 9100L } } },
                        HttpStatusCode.Created);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new GitLabCodeReviewPublicationService(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory,
            TestPostedCommentComposer.Distinctive);

        var diagnostics = await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        // Summary + the second inline still post even though the first inline was rejected.
        Assert.Equal(3, discussionPosts);
        Assert.Equal(2, diagnostics.PostedCount);
        Assert.Equal(1, diagnostics.FailedCount);
        var failure = Assert.Single(diagnostics.PostingFailures);
        Assert.Equal("inline", failure.ThreadKind);
        Assert.Equal("src/a.ts", failure.FilePath);
        Assert.Equal(10, failure.Line);
        Assert.Equal(2, Assert.Single(diagnostics.PostedComments.Where(receipt => receipt.FilePath == "src/a.ts")).FindingOrdinal);
        Assert.Equal(0, failure.FindingOrdinal);
    }

    [Fact]
    public async Task PublishReviewAsync_AllDiscussionsRejected_ThrowsPublicationFailure()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var repository = new RepositoryRef(host, "101", "acme/platform", "acme/platform/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", "start-sha", "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true);
        var result = new ReviewResult(
            "Looks solid overall.",
            [new ReviewComment("src/a.ts", 10, CommentSeverity.Warning, "first inline")]);

        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri.EndsWith("/user", StringComparison.Ordinal))
            {
                return GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" });
            }

            if (uri.EndsWith("/versions", StringComparison.Ordinal))
            {
                return GitLabTestHelpers.CreateJsonResponse(
                    new[]
                    {
                        new { id = 7L, base_commit_sha = "base-sha", head_commit_sha = "head-sha", start_commit_sha = "start-sha" },
                    });
            }

            // Every discussion creation is rejected.
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("rejected") };
        });

        var sut = new GitLabCodeReviewPublicationService(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory,
            TestPostedCommentComposer.Distinctive);

        var exception = await Assert.ThrowsAsync<ReviewCommentPublicationFailedException>(() => sut.PublishReviewAsync(
            clientId, review, revision, result, reviewer));

        Assert.Equal(0, exception.Diagnostics.PostedCount);
        Assert.Equal(2, exception.Diagnostics.FailedCount);
    }

    [Fact]
    public async Task PublishReviewAsync_WhenInlineRevisionLookupFails_StillPostsSummaryAndRecordsInlineFailures()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var repository = new RepositoryRef(host, "101", "acme/platform", "acme/platform/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", "start-sha", "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true);
        var result = new ReviewResult(
            "Looks solid overall.",
            [new ReviewComment("src/a.ts", 10, CommentSeverity.Warning, "first inline")]);

        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri.EndsWith("/user", StringComparison.Ordinal))
            {
                return GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" });
            }

            // The merge-request diff-revision lookup fails, so no inline can be anchored.
            if (uri.EndsWith("/versions", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            if (uri.EndsWith("/discussions", StringComparison.Ordinal))
            {
                return GitLabTestHelpers.CreateJsonResponse(
                    new { id = "discussion-1", notes = new[] { new { id = 9100L } } },
                    HttpStatusCode.Created);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var sut = new GitLabCodeReviewPublicationService(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory,
            TestPostedCommentComposer.Distinctive);

        var diagnostics = await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        // Summary still posts; the inline is recorded as failed rather than aborting the publish.
        Assert.Single(diagnostics.PostedComments);
        var failure = Assert.Single(diagnostics.PostingFailures);
        Assert.Equal("inline", failure.ThreadKind);
        Assert.Equal("src/a.ts", failure.FilePath);
        Assert.Contains("version", failure.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishReviewAsync_EndsTheSummaryAndEveryInlineDiscussionWithTheMarker(bool withRisk)
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var repository = new RepositoryRef(host, "101", "acme/platform", "acme/platform/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", "start-sha", "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true);
        FindingRiskAssessment? assessment = withRisk
            ? new FindingRiskAssessment(
                FindingImpact.Moderate, FindingLikelihood.Unlikely, "Limited local disruption.", "Requires an exceptional retry.", "review_model",
                FindingRiskAssessment.CurrentRubricVersion)
            : null;
        var result = new ReviewResult(
            "Looks solid overall.",
            [
                new ReviewComment("src/file.ts", 18, CommentSeverity.Warning, "Guard this null case.") { RiskAssessment = assessment },
                new ReviewComment(null, null, CommentSeverity.Info, "Overview concern.") { RiskAssessment = assessment },
                new ReviewComment("src/settings.ts", null, CommentSeverity.Warning, "Check the file-level setting.") { RiskAssessment = assessment }
            ]);

        var postedBodies = new List<string>();
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(async request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri == "https://gitlab.example.com/api/v4/projects/101/merge_requests/42/discussions")
            {
                postedBodies.Add(await request.Content!.ReadAsStringAsync());
                return GitLabTestHelpers.CreateJsonResponse(
                    new { id = "discussion-abc", notes = new[] { new { id = 9100L } } },
                    HttpStatusCode.Created);
            }

            return uri switch
            {
                "https://gitlab.example.com/api/v4/user" => GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" }),
                "https://gitlab.example.com/api/v4/projects/101/merge_requests/42/versions" =>
                    GitLabTestHelpers.CreateJsonResponse(
                        new[]
                        {
                            new { id = 7L, base_commit_sha = "base-sha", head_commit_sha = "head-sha", start_commit_sha = "start-sha" },
                        }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });

        var sut = new GitLabCodeReviewPublicationService(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory,
            TestPostedCommentComposer.Distinctive);

        await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        // One discussion for the summary and one for the single inline comment. The marker has to be the last
        // line of each posted body and has to survive the encoding GitLab applies to the body field.
        Assert.Equal(2, postedBodies.Count);
        var decodedBodies = postedBodies
            .Select(body => GitLabTestHelpers.ReadMultipartField(body, "body"))
            .ToList();
        Assert.All(
            decodedBodies,
            body => TestPostedCommentComposer.AssertMarkedOnce(body, TestPostedCommentComposer.DistinctiveMarker));

        var summary = Assert.Single(decodedBodies, body => body.StartsWith("## Meister Review Bot Review", StringComparison.Ordinal));
        Assert.Contains("Looks solid overall.", summary);
        Assert.Contains("- **Warning** · `src/file.ts:18`: Guard this null case.", summary);
        Assert.Contains(withRisk ? "| 3 | Low: 3 | Unassessed: 3 |" : "| 3 | Unassessed: 3 | Unassessed: 3 |", summary);
        Assert.Contains("**`src/settings.ts`**", summary);
        Assert.Contains(withRisk ? "![Risk: Low]" : "\n\nCheck the file-level setting.", summary);
        Assert.DoesNotContain("- Info: Overview concern.", summary);
        Assert.DoesNotContain("- INFO: Overview concern.", summary);
        Assert.Equal(1, summary.Split("Overview concern.", StringSplitOptions.None).Length - 1);
        if (withRisk)
        {
            Assert.Contains(
                "<!-- propr-risk-assessment:start -->\n\n<details>\n<summary>Assessment details</summary>\n\n| Assessment | Level | Reason |", summary);
            Assert.Contains(" |\n\n</details>\n\n<!-- propr-risk-assessment:end -->", summary);
        }

        // Identified by its own text, not by posting order: which discussion GitLab reports back first is not
        // part of the contract this test is verifying.
        var inlineBody = Assert.Single(
            decodedBodies,
            body => body.Contains("Guard this null case.", StringComparison.Ordinal) && !body.StartsWith("## ", StringComparison.Ordinal));
        if (withRisk)
        {
            Assert.StartsWith("![Severity: Warning]", inlineBody);
            Assert.Contains("![Risk: Low]", inlineBody);
            Assert.Contains("\n\nGuard this null case.", inlineBody);
            Assert.Contains("<details>\n<summary>Assessment details</summary>", inlineBody);
            Assert.Contains("| Risk | Low |", inlineBody);
            Assert.Contains("Limited local disruption.", inlineBody);
            Assert.Contains(
                decodedBodies,
                body => body.Contains("Overview concern.", StringComparison.Ordinal) && body.Contains("| Risk | Low |", StringComparison.Ordinal));
        }
        else
        {
            Assert.StartsWith("![Severity: Warning]", inlineBody);
            Assert.Contains("\n\nGuard this null case.\n\n" + TestPostedCommentComposer.DistinctiveMarker, inlineBody);
        }
    }
}
