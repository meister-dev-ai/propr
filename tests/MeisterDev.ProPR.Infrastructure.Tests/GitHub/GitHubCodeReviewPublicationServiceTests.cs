// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using Markdig;
using Markdig.Extensions.Tables;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.GitHub;

public sealed class GitHubCodeReviewPublicationServiceTests
{
    private const string ThreadNodeId = "PRRT_kwDOABCD1234";
    private const string UserUri = "https://api.github.com/user";
    private const string GraphQlUri = "https://api.github.com/graphql";

    private const string ReviewsUri = "https://api.github.com/repos/acme/propr/pulls/42/reviews";

    // The comment listing asks for a page size: left to GitHub's default of thirty, a review posting more
    // comments than that recorded provenance for the first thirty only.
    private const string ReviewCommentsUri =
        "https://api.github.com/repos/acme/propr/pulls/42/reviews/555/comments?per_page=100";

    [Theory]
    [InlineData("```")]
    [InlineData("<div>\n```")]
    [InlineData("Conclusion.")]
    public void BuildPayload_IsolatesConclusionAndCompleteFindingFenceContexts(string conclusion)
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base...head");
        var reviewer = new ReviewerIdentity(host, "99", "bot", "Bot\n```", true);
        var first = new ReviewComment(null, null, CommentSeverity.Warning, "```\nFirst description.");
        var second = new ReviewComment(null, null, CommentSeverity.Warning, "```\n<details>Hidden</details>\n```")
        {
            RiskAssessment = new FindingRiskAssessment(
                FindingImpact.Moderate, FindingLikelihood.Possible, "The state is lost.", "On retry.", "review_model",
                FindingRiskAssessment.CurrentRubricVersion),
        };
        var result = new ReviewResult(conclusion, [first, second]) { ContextSkippedFilePaths = ["src/Path\n```\n.cs"] };

        var payload = GitHubLifecyclePublicationService.BuildPayload(review, revision, result, reviewer, TestPostedCommentComposer.Default);
        var html = Markdown.ToHtml(payload.Body, new MarkdownPipelineBuilder().UsePipeTables(new PipeTableOptions { UseGfmRules = true }).Build());

        Assert.DoesNotContain("<details>Hidden</details>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;details&gt;Hidden&lt;/details&gt;", html, StringComparison.Ordinal);
        Assert.Contains("<table>", html, StringComparison.Ordinal);
        Assert.Contains("Findings to report", html, StringComparison.Ordinal);
        Assert.Contains("src/Path ``` .cs", payload.Body, StringComparison.Ordinal);
        Assert.StartsWith("## Bot ``` Review\n", payload.Body, StringComparison.Ordinal);
        Assert.Equal("```\nFirst description.", first.Message);
        TestPostedCommentComposer.AssertMarkedOnce(payload.Body, TestPostedCommentComposer.Default.Text);
    }

    [Fact]
    public void BuildPayload_PreservesDescriptionCodeAndAutolinksInInlineAndCompleteSummaryFindings()
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "bot", "Review Bot", true);
        const string description = "Use `Task<T>` after reading <https://example.com/x> or contact <reviewer@example.com>.";
        var finding = new ReviewComment("src/Work.cs", 7, CommentSeverity.Warning, description);
        var result = new ReviewResult("Conclusion.", [finding, finding.AsPullRequestLevel(description)]);

        var payload = GitHubLifecyclePublicationService.BuildPayload(review, revision, result, reviewer, TestPostedCommentComposer.Default);

        Assert.StartsWith("![Severity: Warning]", Assert.Single(payload.Comments).Body);
        Assert.Contains("\n\n" + description + "\n\n" + TestPostedCommentComposer.Default.Text, Assert.Single(payload.Comments).Body);
        Assert.Contains("![Severity: Warning]", payload.Body, StringComparison.Ordinal);
        Assert.Contains("\n\n" + description, payload.Body, StringComparison.Ordinal);
        Assert.Equal(description, finding.Message);
    }

    [Fact]
    public void BuildPayload_RendersInlineAndUnanchoredFindingsWithSafeClosedDetails()
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "bot", "    Bot </details><script>bad</script>", true);
        var finding = new ReviewComment("src/</details>.cs", 7, CommentSeverity.Warning, "Guard `a | b`. </details><script>bad</script>")
        {
            RiskAssessment = new FindingRiskAssessment(
                FindingImpact.Moderate, FindingLikelihood.Possible, "Fails `a\\|b`. </details>", "On retry.", "review_model",
                FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.Medium, "The caller is visible."),
        };
        var result = new ReviewResult(
            "Conclusion </details><script>bad</script>.", [finding, finding.AsPullRequestLevel("Check the shared invariant. </details>")])
        {
            BudgetSoftCapped = true,
            BudgetSoftCapSkippedFilePaths = ["```src/</details><script>budget</script>.cs"],
            ContextDegradedFilePaths = ["src/</details><script>degraded</script>.cs"],
            ContextSkippedFilePaths = ["src/</details><script>skipped</script>.cs"],
        };
        var composer = TestPostedCommentComposer.With("Generated </details><script>marker</script>.");

        var payload = GitHubLifecyclePublicationService.BuildPayload(review, revision, result, reviewer, composer);

        var inline = Assert.Single(payload.Comments).Body;
        Assert.StartsWith("![Severity: Warning]", inline);
        Assert.Contains("![Risk: Medium]", inline);
        Assert.Contains("\n\nGuard `a | b`.", inline);
        foreach (var body in new[] { inline, payload.Body })
        {
            Assert.Contains("<details>\n<summary>Assessment details</summary>\n\n| Assessment | Level | Reason |", body);
            Assert.Contains("| Impact | Moderate | Fails `a\\\\|b`. <\u200B/details> |", body, StringComparison.Ordinal);
            Assert.DoesNotContain("<script>", body, StringComparison.Ordinal);
            Assert.Equal(1, body.Split("<details>", StringSplitOptions.None).Length - 1);
            Assert.Equal(1, body.Split("</details>", StringSplitOptions.None).Length - 1);
            TestPostedCommentComposer.AssertMarkedOnce(body, "*Generated <\u200B/details><\u200Bscript>marker<\u200B/script>.*");
        }

        Assert.Equal(1, payload.Body.Split("Check the shared invariant.", StringSplitOptions.None).Length - 1);
        Assert.Contains("| 2 | Medium: 2 | Medium: 2 |", payload.Body);
        Assert.Contains("src/<\u200B/details><\u200Bscript>budget", payload.Body);
        Assert.Contains("src/<\u200B/details><\u200Bscript>degraded", payload.Body);
        Assert.Contains("src/<\u200B/details><\u200Bscript>skipped", payload.Body);
        Assert.Equal("Guard `a | b`. </details><script>bad</script>", finding.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(FindingBadgeStyle.ShieldsIo)]
    [InlineData(FindingBadgeStyle.Plain)]
    public async Task PublishReviewAsync_PostsSummaryAndInlineCommentsToGitHubReviewApi(FindingBadgeStyle? style)
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot[bot]", "Meister Review Bot", true);
        var result = new ReviewResult(
            "Looks solid overall.",
            [
                new ReviewComment("src/file.ts", 18, CommentSeverity.Error, "Guard this null case.")
                {
                    RiskAssessment = new FindingRiskAssessment(
                        FindingImpact.Critical, FindingLikelihood.Likely, "The request loses data.", "Every request uses this branch.",
                        "review_model", FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.High, "The complete path is visible."),
                    FindingClassification = new(
                        [FindingTypeTaxonomy.LogicError], [], null, null, null, FindingTypeTaxonomy.Version, FindingTypeClassification.ReviewSource),
                },
                new ReviewComment(null, null, CommentSeverity.Info, "No blocking issues found."),
            ]);

        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(),
                    clientId,
                    ScmProvider.GitHub,
                    host.HostBaseUrl,
                    ScmAuthenticationKind.PersonalAccessToken,
                    "GitHub",
                    "ghp_test",
                    true));

        string? postedBody = null;
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler(async request =>
            {
                if (request.RequestUri!.AbsoluteUri == "https://api.github.com/user")
                {
                    return CreateJsonResponse(new { login = "meister-dev" });
                }

                if (request.RequestUri.AbsoluteUri ==
                    "https://api.github.com/repos/acme/propr/pulls/42/reviews")
                {
                    postedBody = await request.Content!.ReadAsStringAsync();
                    return CreateJsonResponse(new { id = 1 });
                }

                return CreateJsonResponse(new { message = "Not Found" }, HttpStatusCode.NotFound);
            }));
        httpClientFactory.CreateClient("GitHubProvider")
            .Returns(httpClient);

        var sut = new GitHubCodeReviewPublicationService(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory,
            TestPostedCommentComposer.Distinctive);

        await sut.PublishReviewAsync(
            clientId, review, revision, result, reviewer,
            publicationContext: style.HasValue ? new ReviewPublicationContext(review, revision, reviewer, []) { FindingBadgeStyle = style.Value } : null);

        Assert.NotNull(postedBody);
        using var posted = JsonDocument.Parse(postedBody);
        var inline = Assert.Single(posted.RootElement.GetProperty("comments").EnumerateArray()).GetProperty("body").GetString();
        Assert.StartsWith(style == FindingBadgeStyle.Plain ? "`Severity: Error`" : "![Severity: Error]", inline);
        Assert.Contains("Risk: Critical", inline);
        Assert.Contains("Impact: Critical", inline);
        Assert.Contains("Likelihood: Likely", inline);
        Assert.Contains("Confidence: High", inline);
        var header = inline!.Split("\n\n", StringSplitOptions.None)[0];
        Assert.True(header.IndexOf("Confidence: High", StringComparison.Ordinal) < header.IndexOf("Type: Logic error", StringComparison.Ordinal));
        Assert.DoesNotContain("Highest assessed priority", inline);
        Assert.DoesNotContain("Fix first", inline);
        var summary = posted.RootElement.GetProperty("body").GetString()!;
        Assert.Contains("**Fix first · Risk: Critical** · `src/file.ts:18`: Guard this null case.", summary);
        Assert.True(summary.IndexOf("Fix first", StringComparison.Ordinal) < summary.IndexOf("Looks solid overall.", StringComparison.Ordinal));
        Assert.Contains(style == FindingBadgeStyle.Plain ? "`Severity: Info`" : "![Severity: Info]", posted.RootElement.GetProperty("body").GetString());
        Assert.Contains("Looks solid overall.", postedBody, StringComparison.Ordinal);
        Assert.Contains("Guard this null case.", postedBody, StringComparison.Ordinal);
        Assert.Contains("No blocking issues found.", postedBody, StringComparison.Ordinal);
        Assert.Contains("src/file.ts", postedBody, StringComparison.Ordinal);
        Assert.Contains("head-sha", postedBody, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPayload_LongInlineDescriptionsUseCompactSummaryActionsAndRetainFullComments()
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot[bot]", "Meister Review Bot", true);
        var comments = Enumerable.Range(1, 8)
            .Select(index => new ReviewComment(
                $"src/File{index}.cs", index, CommentSeverity.Warning,
                $"Finding {index}: " + new string('x', 4000)))
            .ToArray();
        var result = new ReviewResult("Preserve the review conclusion.", comments)
        {
            ContextSkippedFilePaths = ["src/Huge.cs"],
        };

        var payload = GitHubLifecyclePublicationService.BuildPayload(review, revision, result, reviewer, TestPostedCommentComposer.Distinctive);

        Assert.StartsWith("## Meister Review Bot Review\n\n" + result.Summary, payload.Body);
        Assert.True(payload.Body.Length < 8000);
        Assert.Contains("| 8 | Unassessed: 8 | Unassessed: 8 |", payload.Body);
        Assert.Contains("src/Huge.cs", payload.Body);
        TestPostedCommentComposer.AssertMarkedOnce(payload.Body, TestPostedCommentComposer.DistinctiveMarker);
        Assert.Equal(8, payload.Comments.Count);
        foreach (var finding in comments)
        {
            Assert.Contains(payload.Comments, comment => comment.Body.Contains("\n\n" + finding.Message, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task PublishReviewAsync_NormalizesInlineCommentPathBeforePosting()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot[bot]", "Meister Review Bot", true);
        var result = new ReviewResult(
            "Looks solid overall.",
            [new ReviewComment("  /src/file.ts  ", 18, CommentSeverity.Warning, "Guard this null case.")]);

        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(),
                    clientId,
                    ScmProvider.GitHub,
                    host.HostBaseUrl,
                    ScmAuthenticationKind.PersonalAccessToken,
                    "GitHub",
                    "ghp_test",
                    true));

        string? postedBody = null;
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler(async request =>
            {
                if (request.RequestUri!.AbsoluteUri == "https://api.github.com/user")
                {
                    return CreateJsonResponse(new { login = "meister-dev" });
                }

                if (request.RequestUri.AbsoluteUri ==
                    "https://api.github.com/repos/acme/propr/pulls/42/reviews")
                {
                    postedBody = await request.Content!.ReadAsStringAsync();
                    return CreateJsonResponse(new { id = 1 });
                }

                return CreateJsonResponse(new { message = "Not Found" }, HttpStatusCode.NotFound);
            }));
        httpClientFactory.CreateClient("GitHubProvider")
            .Returns(httpClient);

        var sut = new GitHubCodeReviewPublicationService(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory,
            TestPostedCommentComposer.Distinctive);

        await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        Assert.NotNull(postedBody);
        using var document = JsonDocument.Parse(postedBody);
        var comments = document.RootElement.GetProperty("comments");
        Assert.Equal("src/file.ts", comments[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task PublishReviewAsync_WhenReviewHasNoInlineComments_SendsEmptyCommentsArray()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot[bot]", "Meister Review Bot", true);
        var result = new ReviewResult(
            "Looks solid overall.",
            [new ReviewComment(null, null, CommentSeverity.Info, "No blocking issues found.")]);

        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(),
                    clientId,
                    ScmProvider.GitHub,
                    host.HostBaseUrl,
                    ScmAuthenticationKind.PersonalAccessToken,
                    "GitHub",
                    "ghp_test",
                    true));

        string? postedBody = null;
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler(async request =>
            {
                if (request.RequestUri!.AbsoluteUri == "https://api.github.com/user")
                {
                    return CreateJsonResponse(new { login = "meister-dev" });
                }

                if (request.RequestUri.AbsoluteUri ==
                    "https://api.github.com/repos/acme/propr/pulls/42/reviews")
                {
                    postedBody = await request.Content!.ReadAsStringAsync();
                    return CreateJsonResponse(new { id = 1 });
                }

                return CreateJsonResponse(new { message = "Not Found" }, HttpStatusCode.NotFound);
            }));
        httpClientFactory.CreateClient("GitHubProvider")
            .Returns(httpClient);

        var sut = new GitHubCodeReviewPublicationService(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory,
            TestPostedCommentComposer.Distinctive);

        await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        Assert.NotNull(postedBody);
        Assert.Contains("\"comments\":[]", postedBody, StringComparison.Ordinal);
        Assert.Contains("No blocking issues found.", postedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishReviewAsync_IgnoresAdditivePublicationContextForGitHub()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot[bot]", "Meister Review Bot", true);
        var result = new ReviewResult("Looks solid overall.", []);
        var publicationContext = new ReviewPublicationContext(
            review,
            revision,
            reviewer,
            [new PrCommentThread("1", "src/file.ts", 18, [new PrThreadComment("Bot", "Existing thread")])]);

        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(),
                    clientId,
                    ScmProvider.GitHub,
                    host.HostBaseUrl,
                    ScmAuthenticationKind.PersonalAccessToken,
                    "GitHub",
                    "ghp_test",
                    true));

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler(request => Task.FromResult(
                request.RequestUri!.AbsoluteUri switch
                {
                    "https://api.github.com/user" => CreateJsonResponse(new { login = "meister-dev" }),
                    "https://api.github.com/repos/acme/propr/pulls/42/reviews" => CreateJsonResponse(new { id = 1 }),
                    _ => CreateJsonResponse(new { message = "Not Found" }, HttpStatusCode.NotFound),
                })));
        httpClientFactory.CreateClient("GitHubProvider").Returns(httpClient);

        var sut = new GitHubCodeReviewPublicationService(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory,
            TestPostedCommentComposer.Distinctive);

        var diagnostics = await sut.PublishReviewAsync(clientId, review, revision, result, reviewer, publicationContext: publicationContext);

        Assert.Equal(0, diagnostics.PostedCount);
        Assert.Equal(0, diagnostics.SuppressedCount);
    }

    [Fact]
    public async Task PublishReviewAsync_WhenGitHubReturnsValidationError_IncludesResponseBodyInException()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot[bot]", "Meister Review Bot", true);
        var result = new ReviewResult(
            "Looks solid overall.",
            [new ReviewComment("src/file.ts", 18, CommentSeverity.Warning, "Guard this null case.")]);

        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(),
                    clientId,
                    ScmProvider.GitHub,
                    host.HostBaseUrl,
                    ScmAuthenticationKind.PersonalAccessToken,
                    "GitHub",
                    "ghp_test",
                    true));

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler(request => Task.FromResult(
                request.RequestUri!.AbsoluteUri switch
                {
                    "https://api.github.com/user" => CreateJsonResponse(new { login = "meister-dev" }),
                    "https://api.github.com/repos/acme/propr/pulls/42/reviews" => CreateJsonResponse(
                        new { message = "Review comments is invalid and Review threads is invalid" },
                        (HttpStatusCode)422),
                    _ => CreateJsonResponse(new { message = "Not Found" }, HttpStatusCode.NotFound),
                })));
        httpClientFactory.CreateClient("GitHubProvider")
            .Returns(httpClient);

        var sut = new GitHubCodeReviewPublicationService(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory,
            TestPostedCommentComposer.Distinctive);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.PublishReviewAsync(clientId, review, revision, result, reviewer));

        Assert.Contains("422", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Review comments is invalid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishReviewAsync_AppInstallation_UsesInstallationAccessToken()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot[bot]", "Meister Review Bot", true);
        var result = new ReviewResult("Looks solid overall.", []);

        var connectionRepository = GitHubAppTestHelpers.CreateAppInstallationConnectionRepository(clientId, host);
        string? reviewAuthorization = null;
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler(request => Task.FromResult(
                request.RequestUri!.AbsoluteUri switch
                {
                    "https://api.github.com/app/installations/789012" => CreateJsonResponse(new { account = new { login = "acme-platform" } }),
                    "https://api.github.com/app/installations/789012/access_tokens" => CreateJsonResponse(
                        new
                        {
                            token = "installation-token",
                            expires_at = DateTimeOffset.UtcNow.AddHours(1),
                        }),
                    "https://api.github.com/repos/acme/propr/pulls/42/reviews" => CaptureAndReturnReviewResponse(request),
                    _ => CreateJsonResponse(new { message = "Not Found" }, HttpStatusCode.NotFound),
                })));
        httpClientFactory.CreateClient("GitHubProvider").Returns(httpClient);

        var sut = new GitHubCodeReviewPublicationService(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory,
            TestPostedCommentComposer.Distinctive);

        await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        Assert.Equal("installation-token", reviewAuthorization);
        return;

        HttpResponseMessage CaptureAndReturnReviewResponse(HttpRequestMessage request)
        {
            reviewAuthorization = request.Headers.Authorization?.Parameter;
            return CreateJsonResponse(new { id = 1 });
        }
    }

    [Fact]
    public async Task PublishReviewAsync_AppInstallationPermissionLoss_ThrowsActionableInvalidOperationException()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot[bot]", "Meister Review Bot", true);
        var result = new ReviewResult("Looks solid overall.", []);

        var connectionRepository = GitHubAppTestHelpers.CreateAppInstallationConnectionRepository(clientId, host);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler(request => Task.FromResult(
                request.RequestUri!.AbsoluteUri switch
                {
                    "https://api.github.com/app/installations/789012" => CreateJsonResponse(new { account = new { login = "acme-platform" } }),
                    "https://api.github.com/app/installations/789012/access_tokens" => CreateJsonResponse(
                        new
                        {
                            token = "installation-token",
                            expires_at = DateTimeOffset.UtcNow.AddHours(1),
                        }),
                    "https://api.github.com/repos/acme/propr/pulls/42/reviews" => CreateJsonResponse(
                        new { message = "Resource not accessible by integration" },
                        HttpStatusCode.Forbidden),
                    _ => CreateJsonResponse(new { message = "Not Found" }, HttpStatusCode.NotFound),
                })));
        httpClientFactory.CreateClient("GitHubProvider").Returns(httpClient);

        var sut = new GitHubCodeReviewPublicationService(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory,
            TestPostedCommentComposer.Distinctive);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.PublishReviewAsync(clientId, review, revision, result, reviewer));

        Assert.Contains("no longer has permission", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Resource not accessible by integration", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublishReviewAsync_RecordsAThreadIdTheThreadStatusWriterAccepts()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot[bot]", "Meister Review Bot", true);
        var result = new ReviewResult(
            "Looks solid overall.",
            [new ReviewComment("src/file.ts", 18, CommentSeverity.Warning, "Guard this null case.")]);

        var connectionRepository = CreateConnectionRepository(clientId, host);

        string? mutationBody = null;
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler(async request =>
            {
                if (request.RequestUri!.AbsoluteUri == GraphQlUri)
                {
                    var graphQlBody = await request.Content!.ReadAsStringAsync();
                    if (graphQlBody.Contains("resolveReviewThread", StringComparison.Ordinal))
                    {
                        mutationBody = graphQlBody;
                        return CreateJsonResponse(new { data = new { resolveReviewThread = new { thread = new { id = ThreadNodeId, isResolved = true } } } });
                    }

                    return CreateReviewThreadsResponse(ThreadNodeId, 9001L);
                }

                return request.RequestUri.AbsoluteUri switch
                {
                    UserUri => CreateJsonResponse(new { login = "meister-dev" }),
                    ReviewsUri => CreateJsonResponse(new { id = 555L }),
                    ReviewCommentsUri => CreateJsonResponse(new[] { new { id = 9001L, path = "src/file.ts", line = 18 } }),
                    _ => CreateJsonResponse(new { message = "Not Found" }, HttpStatusCode.NotFound),
                };
            }));
        httpClientFactory.CreateClient("GitHubProvider").Returns(httpClient);

        var publicationService = new GitHubCodeReviewPublicationService(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory,
            TestPostedCommentComposer.Distinctive);

        var diagnostics = await publicationService.PublishReviewAsync(clientId, review, revision, result, reviewer);

        var reference = Assert.Single(diagnostics.PostedComments);
        Assert.NotNull(reference.ProviderThreadId);

        // The identifier publishing recorded is handed straight to the writer that resolves the thread, which
        // is the journey that made a review id recorded here reach a call expecting a thread node id.
        var statusWriter = new GitHubReviewThreadStatusWriter(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);
        var thread = new ReviewThreadRef(
            review,
            reference.ProviderThreadId!,
            reference.FilePath,
            reference.Line,
            isReviewerOwned: true);

        await statusWriter.UpdateThreadStatusAsync(clientId, thread, "fixed");

        Assert.NotNull(mutationBody);
        Assert.Contains(ThreadNodeId, mutationBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishReviewAsync_WhenTheThreadLookupIsRefused_RecordsCommentIdsWithoutAThreadId()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot[bot]", "Meister Review Bot", true);
        var result = new ReviewResult(
            "Looks solid overall.",
            [new ReviewComment("src/file.ts", 18, CommentSeverity.Warning, "Guard this null case.")]);

        var connectionRepository = CreateConnectionRepository(clientId, host);

        // GitHub refuses a query with two hundred and an errors array, and answers a partial refusal with a
        // data section alongside it. Ids read out of a traversal that reported an error are not trustworthy
        // enough to key a later write on, so the whole lookup is discarded rather than half believed.
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler(request => Task.FromResult(
                request.RequestUri!.AbsoluteUri switch
                {
                    UserUri => CreateJsonResponse(new { login = "meister-dev" }),
                    ReviewsUri => CreateJsonResponse(new { id = 555L }),
                    ReviewCommentsUri => CreateJsonResponse(new[] { new { id = 9001L, path = "src/file.ts", line = 18 } }),
                    GraphQlUri => CreateRefusedReviewThreadsResponse(ThreadNodeId, 9001L),
                    _ => CreateJsonResponse(new { message = "Not Found" }, HttpStatusCode.NotFound),
                })));
        httpClientFactory.CreateClient("GitHubProvider").Returns(httpClient);

        var sut = new GitHubCodeReviewPublicationService(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory,
            TestPostedCommentComposer.Distinctive);

        var diagnostics = await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        var reference = Assert.Single(diagnostics.PostedComments);
        Assert.Equal("9001", reference.ProviderCommentId);
        Assert.Null(reference.ProviderThreadId);
        Assert.Equal(1, diagnostics.PostedCount);
    }

    [Fact]
    public async Task PublishReviewAsync_CapturesCreatedCommentIdsFromReviewCommentsEndpoint()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot[bot]", "Meister Review Bot", true);
        var result = new ReviewResult(
            "Looks solid overall.",
            [new ReviewComment("src/file.ts", 18, CommentSeverity.Warning, "Guard this null case.")]);

        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(),
                    clientId,
                    ScmProvider.GitHub,
                    host.HostBaseUrl,
                    ScmAuthenticationKind.PersonalAccessToken,
                    "GitHub",
                    "ghp_test",
                    true));

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler(request => Task.FromResult(
                request.RequestUri!.AbsoluteUri switch
                {
                    UserUri => CreateJsonResponse(new { login = "meister-dev" }),
                    ReviewsUri => CreateJsonResponse(new { id = 555L }),
                    ReviewCommentsUri => CreateJsonResponse(
                        new[]
                        {
                            new { id = 9001L, path = "src/file.ts", line = 18 },
                        }),
                    GraphQlUri => CreateReviewThreadsResponse(ThreadNodeId, 9001L),
                    _ => CreateJsonResponse(new { message = "Not Found" }, HttpStatusCode.NotFound),
                })));
        httpClientFactory.CreateClient("GitHubProvider").Returns(httpClient);

        var sut = new GitHubCodeReviewPublicationService(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory,
            TestPostedCommentComposer.Distinctive);

        var diagnostics = await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        var reference = Assert.Single(diagnostics.PostedComments);
        Assert.Equal("9001", reference.ProviderCommentId);

        // The node id of the thread the comment opened, which addresses a GitHub review thread. The
        // review id the same response also carries names a different object and can never address one.
        Assert.Equal(ThreadNodeId, reference.ProviderThreadId);
        Assert.Equal("src/file.ts", reference.FilePath);
        Assert.Equal(18, reference.Line);
        Assert.Equal(1, diagnostics.PostedCount);
    }

    [Fact]
    public async Task PublishReviewAsync_WhenCommentsEndpointUnavailable_PublishesWithEmptyPostedComments()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot[bot]", "Meister Review Bot", true);
        var result = new ReviewResult(
            "Looks solid overall.",
            [new ReviewComment("src/file.ts", 18, CommentSeverity.Warning, "Guard this null case.")]);

        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(),
                    clientId,
                    ScmProvider.GitHub,
                    host.HostBaseUrl,
                    ScmAuthenticationKind.PersonalAccessToken,
                    "GitHub",
                    "ghp_test",
                    true));

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler(request => Task.FromResult(
                request.RequestUri!.AbsoluteUri switch
                {
                    "https://api.github.com/user" => CreateJsonResponse(new { login = "meister-dev" }),
                    "https://api.github.com/repos/acme/propr/pulls/42/reviews" => CreateJsonResponse(new { id = 555L }),
                    ReviewCommentsUri => CreateJsonResponse(
                        new { message = "Not Found" },
                        HttpStatusCode.NotFound),
                    _ => CreateJsonResponse(new { message = "Not Found" }, HttpStatusCode.NotFound),
                })));
        httpClientFactory.CreateClient("GitHubProvider").Returns(httpClient);

        var sut = new GitHubCodeReviewPublicationService(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory,
            TestPostedCommentComposer.Distinctive);

        var diagnostics = await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        Assert.Empty(diagnostics.PostedComments);
        Assert.Equal(1, diagnostics.PostedCount);
    }

    private static HttpResponseMessage CreateJsonResponse<T>(T payload, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload)),
        };
    }

    // The pull request's review threads as GraphQL reports them: each thread's node id alongside the database
    // ids of its comments, which is the only join between a created review comment and its thread.
    private static HttpResponseMessage CreateReviewThreadsResponse(string threadNodeId, long commentDatabaseId)
    {
        return CreateJsonResponse(new { data = BuildReviewThreadsData(threadNodeId, commentDatabaseId) });
    }

    private static HttpResponseMessage CreateRefusedReviewThreadsResponse(string threadNodeId, long commentDatabaseId)
    {
        return CreateJsonResponse(
            new
            {
                data = BuildReviewThreadsData(threadNodeId, commentDatabaseId),
                errors = new[] { new { type = "FORBIDDEN", message = "Resource not accessible by integration" } },
            });
    }

    private static object BuildReviewThreadsData(string threadNodeId, long commentDatabaseId)
    {
        return new
        {
            repository = new
            {
                pullRequest = new
                {
                    reviewThreads = new
                    {
                        nodes = new[]
                        {
                            new
                            {
                                id = threadNodeId,
                                comments = new
                                {
                                    nodes = new[] { new { databaseId = commentDatabaseId } },
                                },
                            },
                        },
                    },
                },
            },
        };
    }

    private static IClientScmConnectionRepository CreateConnectionRepository(Guid clientId, ProviderHostRef host)
    {
        var repository = Substitute.For<IClientScmConnectionRepository>();
        repository.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(),
                    clientId,
                    ScmProvider.GitHub,
                    host.HostBaseUrl,
                    ScmAuthenticationKind.PersonalAccessToken,
                    "GitHub",
                    "ghp_test",
                    true));
        return repository;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await responder(request);
            return new DelegatingDisposableHttpResponseMessage(response);
        }

        private sealed class DelegatingDisposableHttpResponseMessage : HttpResponseMessage
        {
            private readonly HttpResponseMessage _inner;

            public DelegatingDisposableHttpResponseMessage(HttpResponseMessage inner)
                : base(inner.StatusCode)
            {
                this._inner = inner;
                this.ReasonPhrase = inner.ReasonPhrase;
                this.Version = inner.Version;
                this.RequestMessage = inner.RequestMessage;
                this.Content = inner.Content;

                foreach (var header in inner.Headers)
                {
                    this.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    if (ReferenceEquals(this.Content, this._inner.Content))
                    {
                        this.Content = null;
                    }

                    base.Dispose(true);
                    this._inner.Dispose();
                    return;
                }

                base.Dispose(false);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishReviewAsync_EndsTheReviewBodyAndEveryInlineCommentWithTheMarker(bool withRisk)
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot[bot]", "Meister Review Bot", true);
        FindingRiskAssessment? assessment = withRisk
            ? new FindingRiskAssessment(
                FindingImpact.Moderate, FindingLikelihood.Unlikely, "Limited local disruption.", "Requires an exceptional retry.", "review_model",
                FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.Certain, "A deterministic retry reproduction confirms the failure.")
            : null;
        var result = new ReviewResult(
            "Looks solid overall.",
            [
                new ReviewComment("src/file.ts", 18, CommentSeverity.Warning, "Guard this null case.") { RiskAssessment = assessment },
                new ReviewComment(null, null, CommentSeverity.Info, "No blocking issues found.") { RiskAssessment = assessment },
                new ReviewComment("src/settings.ts", null, CommentSeverity.Warning, "Check the file-level setting.") { RiskAssessment = assessment },
            ]);

        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(),
                    clientId,
                    ScmProvider.GitHub,
                    host.HostBaseUrl,
                    ScmAuthenticationKind.PersonalAccessToken,
                    "GitHub",
                    "ghp_test",
                    true));

        string? postedBody = null;
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler(async request =>
            {
                if (request.RequestUri!.AbsoluteUri == UserUri)
                {
                    return CreateJsonResponse(new { login = "meister-dev" });
                }

                if (request.RequestUri.AbsoluteUri == ReviewsUri)
                {
                    postedBody = await request.Content!.ReadAsStringAsync();
                    return CreateJsonResponse(new { id = 1 });
                }

                return CreateJsonResponse(new { message = "Not Found" }, HttpStatusCode.NotFound);
            }));
        httpClientFactory.CreateClient("GitHubProvider").Returns(httpClient);

        var sut = new GitHubCodeReviewPublicationService(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory,
            TestPostedCommentComposer.Distinctive);

        await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        Assert.NotNull(postedBody);
        const string marker = TestPostedCommentComposer.DistinctiveMarker;
        using var payload = JsonDocument.Parse(postedBody);
        TestPostedCommentComposer.AssertMarkedOnce(payload.RootElement.GetProperty("body").GetString(), marker);
        var summary = payload.RootElement.GetProperty("body").GetString()!;
        Assert.Contains("Looks solid overall.", summary);
        Assert.Contains("- **Warning** · `src/file.ts:18`: Guard this null case.", summary);
        Assert.Contains(withRisk ? "| 3 | Low: 3 | Certain: 3 |" : "| 3 | Unassessed: 3 | Unassessed: 3 |", summary);
        Assert.Contains("**`src/settings.ts`**", summary);
        Assert.Contains(withRisk ? "![Risk: Low]" : "\n\nCheck the file-level setting.", summary);
        Assert.DoesNotContain("- Info: No blocking issues found.", summary);
        Assert.DoesNotContain("- INFO: No blocking issues found.", summary);
        Assert.Equal(1, summary.Split("No blocking issues found.", StringSplitOptions.None).Length - 1);
        if (withRisk)
        {
            Assert.Contains(
                "\n\n<!-- propr-risk-assessment:start -->\n\n<details>\n<summary>Assessment details</summary>\n\n| Assessment | Level | Reason |", summary);
        }


        // One inline comment for the one comment anchored to a file; the comment without a file belongs in the
        // review body. A count assertion is what keeps a dropped inline comment from reading as success.
        var comments = payload.RootElement.GetProperty("comments").EnumerateArray().ToList();
        var comment = Assert.Single(comments);
        Assert.Equal("src/file.ts", comment.GetProperty("path").GetString());
        var inlineBody = comment.GetProperty("body").GetString();
        if (withRisk)
        {
            Assert.Contains("| Risk | Low |", inlineBody);
            Assert.Contains("Limited local disruption.", inlineBody);
            Assert.Contains("| Confidence | Certain |", inlineBody);
            Assert.Contains("A deterministic retry reproduction confirms the failure.", inlineBody);
            Assert.Contains("| Risk | Low |", payload.RootElement.GetProperty("body").GetString());
        }
        else
        {
            Assert.StartsWith("![Severity: Warning]", inlineBody);
            Assert.Contains("\n\nGuard this null case.\n\n" + marker, inlineBody);
        }
    }
}
