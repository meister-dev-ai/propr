// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Security;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Forgejo;

public sealed class ForgejoPublicationContextContractTests
{
    [Theory]
    [InlineData(FindingBadgeStyle.ShieldsIo)]
    [InlineData(FindingBadgeStyle.Plain)]
    public async Task PublishReviewAsync_LabelsAndHighlightsStructuredReplacementsInInlineAndSummaryFindings(FindingBadgeStyle style)
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var revision = new ReviewRevision("aabbccddeeff00112233", "00112233445566778899", null, "aabbccddeeff00112233", "base...head");
        var reviewer = new ReviewerIdentity(host, "99", "bot", "Review Bot", true);
        const string replacement = "    return value<T>();\r\n\tNext();\r\n";
        var finding = ReviewSuggestedFixValidator.Bind(
            new ReviewComment("src/Work.cs", 2, CommentSeverity.Error, "The exact input loses data.")
            {
                SuggestedFix = new ReviewSuggestedFix(2, 2, "old();", replacement),
                RiskAssessment = new FindingRiskAssessment(
                    FindingImpact.Critical, FindingLikelihood.Likely, "The request loses data.", "Every request uses this branch.",
                    "review_model", FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.High, "The complete path is visible."),
            },
            [new ChangedFile("src/Work.cs", ChangeType.Edit, "before();\nold();", "@@ -1,2 +1,2 @@\n before();\n+old();")], revision.HeadSha);
        var result = new ReviewResult("Conclusion.", [finding, finding.AsPullRequestLevel(finding.Message)]);
        string? postedReview = null;
        var connectionRepository = ForgejoTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = ForgejoTestHelpers.CreateHttpClientFactory(async request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri == "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews" && request.Method == HttpMethod.Post)
            {
                postedReview = await request.Content!.ReadAsStringAsync();
                return ForgejoTestHelpers.CreateJsonResponse(new { id = 9001L });
            }

            return uri switch
            {
                "https://codeberg.example.com/api/v1/user" => ForgejoTestHelpers.CreateJsonResponse(new { login = "meister-dev" }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews?limit=100" => ForgejoTestHelpers.CreateJsonResponse(
                    Array.Empty<object>()),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });
        var sut = new ForgejoCodeReviewPublicationService(
            new ForgejoConnectionVerifier(connectionRepository, httpClientFactory), httpClientFactory, TestPostedCommentComposer.Default);

        await sut.PublishReviewAsync(
            clientId, review, revision, result, reviewer,
            publicationContext: new ReviewPublicationContext(review, revision, reviewer, []) { FindingBadgeStyle = style });

        Assert.NotNull(postedReview);
        using var payload = JsonDocument.Parse(postedReview);
        var inline = Assert.Single(payload.RootElement.GetProperty("comments").EnumerateArray());
        Assert.Equal("src/Work.cs", inline.GetProperty("path").GetString());
        Assert.Equal(2, inline.GetProperty("new_position").GetInt32());
        var body = inline.GetProperty("body").GetString()!;
        var summary = payload.RootElement.GetProperty("body").GetString()!;
        foreach (var content in new[] { body, summary })
        {
            Assert.Contains(
                "<!-- propr-suggested-fix:start -->\n\n**Suggested change**\n\n```csharp\n" + replacement
                                                                                            + "```\n\n<!-- propr-suggested-fix:end -->", content);
            Assert.DoesNotContain("```suggestion", content);
            Assert.Equal(style == FindingBadgeStyle.ShieldsIo, content.Contains("img.shields.io", StringComparison.Ordinal));
            Assert.Contains("Risk: Critical", content);
            Assert.Contains("<details>\n<summary>Assessment details</summary>", content);
            Assert.Contains("The complete path is visible.", content);
            Assert.EndsWith(TestPostedCommentComposer.Default.Text, content);
        }

        Assert.Equal(
            "ERROR: " + finding.Message,
            ReviewFindingTextFormatter.StripAssessment(body.Split("\n\n" + TestPostedCommentComposer.Default.Text, StringSplitOptions.None)[0]));
        Assert.True(finding.SuggestedFix!.IsSourceValidated);
        Assert.Equal(replacement, finding.SuggestedFix.ReplacementText);
        Assert.Equal("The exact input loses data.", finding.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(FindingBadgeStyle.ShieldsIo)]
    [InlineData(FindingBadgeStyle.Plain)]
    public async Task PublishReviewAsync_PreservesDescriptionCodeAndAutolinksInInlineAndCompleteSummaryFindings(FindingBadgeStyle? style)
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var revision = new ReviewRevision("aabbccddeeff00112233", "00112233445566778899", null, "aabbccddeeff00112233", "base...head");
        var reviewer = new ReviewerIdentity(host, "99", "bot", "Review Bot", true);
        const string description = "Use `Task<T>` after reading <https://example.com/x> or contact <reviewer@example.com>.";
        var finding = new ReviewComment("src/Work.cs", 7, CommentSeverity.Error, description)
        {
            RiskAssessment = new FindingRiskAssessment(
                FindingImpact.Critical, FindingLikelihood.Likely, "The request loses data.", "Every request uses this branch.",
                "review_model", FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.High, "The complete path is visible."),
            FindingClassification = new(
                [FindingTypeTaxonomy.LogicError], [], null, null, null, FindingTypeTaxonomy.Version, FindingTypeClassification.ReviewSource),
        };
        var result = new ReviewResult("Conclusion.", [finding, finding.AsPullRequestLevel(description)]);
        string? postedReview = null;
        var connectionRepository = ForgejoTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = ForgejoTestHelpers.CreateHttpClientFactory(async request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri == "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews" && request.Method == HttpMethod.Post)
            {
                postedReview = await request.Content!.ReadAsStringAsync();
                return ForgejoTestHelpers.CreateJsonResponse(new { id = 9001L });
            }

            return uri switch
            {
                "https://codeberg.example.com/api/v1/user" => ForgejoTestHelpers.CreateJsonResponse(new { login = "meister-dev" }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews?limit=100" => ForgejoTestHelpers.CreateJsonResponse(
                    Array.Empty<object>()),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });
        var sut = new ForgejoCodeReviewPublicationService(
            new ForgejoConnectionVerifier(connectionRepository, httpClientFactory), httpClientFactory, TestPostedCommentComposer.Default);

        await sut.PublishReviewAsync(
            clientId, review, revision, result, reviewer,
            publicationContext: style.HasValue ? new ReviewPublicationContext(review, revision, reviewer, []) { FindingBadgeStyle = style.Value } : null);

        Assert.NotNull(postedReview);
        using var payload = JsonDocument.Parse(postedReview);
        var inline = Assert.Single(payload.RootElement.GetProperty("comments").EnumerateArray());
        var body = inline.GetProperty("body").GetString()!;
        Assert.Contains("\n\n" + description + "\n\n<!-- propr-risk-assessment:start -->", body);
        var prefix = style == FindingBadgeStyle.Plain ? "`Severity: Error`" : "![Severity: Error]";
        Assert.StartsWith(prefix, body);
        Assert.Contains("Risk: Critical", body);
        Assert.Contains("Impact: Critical", body);
        Assert.Contains("Likelihood: Likely", body);
        Assert.Contains("Confidence: High", body);
        var header = body.Split("\n\n", StringSplitOptions.None)[0];
        Assert.True(header.IndexOf("Confidence: High", StringComparison.Ordinal) < header.IndexOf("Type: Logic error", StringComparison.Ordinal));
        Assert.DoesNotContain("Highest assessed priority", body);
        Assert.DoesNotContain("Fix first", body);
        Assert.EndsWith(TestPostedCommentComposer.Default.Text, body);
        var summary = payload.RootElement.GetProperty("body").GetString()!;
        Assert.Contains(prefix, summary, StringComparison.Ordinal);
        Assert.Contains("\n\n" + description, summary, StringComparison.Ordinal);
        Assert.Contains("**Fix first · Risk: Critical** · `src/Work.cs:7`: Use ", summary);
        Assert.True(summary.IndexOf("Fix first", StringComparison.Ordinal) < summary.IndexOf("Conclusion.", StringComparison.Ordinal));
        Assert.EndsWith(TestPostedCommentComposer.Default.Text, summary);
        Assert.Equal(description, finding.Message);
    }

    [Fact]
    public void ProviderAdapters_RegisterForgejoPublicationUnderNeutralInterface()
    {
        var services = new ServiceCollection();

        services.AddSingleton(Substitute.For<IClientScmConnectionRepository>());
        services.AddForgejoProviderAdapters();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var publicationService = scope.ServiceProvider
            .GetServices<ICodeReviewPublicationService>()
            .Single(service => service.Provider == ScmProvider.Forgejo);

        Assert.IsType<ForgejoCodeReviewPublicationService>(publicationService);
    }

    [Fact]
    public async Task PublishReviewAsync_AcceptsPublicationContextWithoutChangingResultAccounting()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var revision = new ReviewRevision(
            "aabbccddeeff00112233445566778899aabbccdd",
            "00112233445566778899aabbccddeeff00112233",
            "00112233445566778899aabbccddeeff00112233",
            "aabbccddeeff00112233445566778899aabbccdd",
            "00112233445566778899aabbccddeeff00112233...aabbccddeeff00112233445566778899aabbccdd");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true);
        var result = new ReviewResult("Looks solid overall.", []);
        var publicationContext = new ReviewPublicationContext(
            review,
            revision,
            reviewer,
            [new PrCommentThread("1", null, null, [new PrThreadComment("Bot", "Summary")])]);
        var connectionRepository = ForgejoTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = ForgejoTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://codeberg.example.com/api/v1/user" => ForgejoTestHelpers.CreateJsonResponse(new { login = "meister-dev" }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews?limit=100" =>
                    ForgejoTestHelpers.CreateJsonResponse(Array.Empty<object>()),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews" =>
                    ForgejoTestHelpers.CreateJsonResponse(new { id = 9001 }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new ForgejoCodeReviewPublicationService(
            new ForgejoConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory,
            TestPostedCommentComposer.Distinctive);

        var diagnostics = await sut.PublishReviewAsync(clientId, review, revision, result, reviewer, publicationContext: publicationContext);

        Assert.Equal(0, diagnostics.PostedCount);
        Assert.Equal(0, diagnostics.SuppressedCount);
        Assert.Equal(0, diagnostics.CandidateCount);
    }

    [Fact]
    public async Task PublishReviewAsync_CapturesCreatedReviewCommentIds()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var revision = new ReviewRevision(
            "aabbccddeeff00112233445566778899aabbccdd",
            "00112233445566778899aabbccddeeff00112233",
            "00112233445566778899aabbccddeeff00112233",
            "aabbccddeeff00112233445566778899aabbccdd",
            "00112233445566778899aabbccddeeff00112233...aabbccddeeff00112233445566778899aabbccdd");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true);
        var result = new ReviewResult(
            "Looks solid overall.",
            [new ReviewComment("src/file.ts", 18, CommentSeverity.Warning, "Guard this null case.")]);

        var connectionRepository = ForgejoTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = ForgejoTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://codeberg.example.com/api/v1/user" => ForgejoTestHelpers.CreateJsonResponse(new { login = "meister-dev" }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews?limit=100" =>
                    ForgejoTestHelpers.CreateJsonResponse(Array.Empty<object>()),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews" =>
                    ForgejoTestHelpers.CreateJsonResponse(new { id = 9001L }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews/9001/comments" =>
                    ForgejoTestHelpers.CreateJsonResponse(
                        new[]
                        {
                            new { id = 4242, path = "src/file.ts", position = 18, original_position = (int?)null },
                        }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new ForgejoCodeReviewPublicationService(
            new ForgejoConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory,
            TestPostedCommentComposer.Distinctive);

        var diagnostics = await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        var reference = Assert.Single(diagnostics.PostedComments);
        Assert.Equal("4242", reference.ProviderCommentId);
        Assert.Equal("9001", reference.ProviderThreadId);
        Assert.Equal("src/file.ts", reference.FilePath);
        Assert.Equal(18, reference.Line);
        Assert.Equal(1, diagnostics.PostedCount);
    }

    [Fact]
    public async Task PublishReviewAsync_WhenReviewCommentsEndpointUnavailable_PublishesWithEmptyPostedComments()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var revision = new ReviewRevision(
            "aabbccddeeff00112233445566778899aabbccdd",
            "00112233445566778899aabbccddeeff00112233",
            "00112233445566778899aabbccddeeff00112233",
            "aabbccddeeff00112233445566778899aabbccdd",
            "00112233445566778899aabbccddeeff00112233...aabbccddeeff00112233445566778899aabbccdd");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot", "Meister Review Bot", true);
        var result = new ReviewResult(
            "Looks solid overall.",
            [new ReviewComment("src/file.ts", 18, CommentSeverity.Warning, "Guard this null case.")]);

        var connectionRepository = ForgejoTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = ForgejoTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri switch
            {
                "https://codeberg.example.com/api/v1/user" => ForgejoTestHelpers.CreateJsonResponse(new { login = "meister-dev" }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews?limit=100" =>
                    ForgejoTestHelpers.CreateJsonResponse(Array.Empty<object>()),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews" =>
                    ForgejoTestHelpers.CreateJsonResponse(new { id = 9001L }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews/9001/comments" =>
                    ForgejoTestHelpers.CreateJsonResponse(new { message = "Not Found" }, HttpStatusCode.NotFound),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });

        var sut = new ForgejoCodeReviewPublicationService(
            new ForgejoConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory,
            TestPostedCommentComposer.Distinctive);

        var diagnostics = await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        Assert.Empty(diagnostics.PostedComments);
        Assert.Equal(1, diagnostics.PostedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishReviewAsync_EndsTheReviewBodyAndEveryInlineCommentWithTheMarker(bool withRisk)
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        var revision = new ReviewRevision(
            "aabbccddeeff00112233445566778899aabbccdd",
            "00112233445566778899aabbccddeeff00112233",
            "00112233445566778899aabbccddeeff00112233",
            "aabbccddeeff00112233445566778899aabbccdd",
            "00112233445566778899aabbccddeeff00112233...aabbccddeeff00112233445566778899aabbccdd");
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
                new ReviewComment(null, null, CommentSeverity.Info, "No blocking issues found.") { RiskAssessment = assessment },
                new ReviewComment("src/settings.ts", null, CommentSeverity.Warning, "Check the file-level setting.") { RiskAssessment = assessment },
            ]);

        string? postedReview = null;
        var connectionRepository = ForgejoTestHelpers.CreateConnectionRepository(clientId, host);
        var httpClientFactory = ForgejoTestHelpers.CreateHttpClientFactory(async request =>
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (uri == "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews"
                && request.Method == HttpMethod.Post)
            {
                postedReview = await request.Content!.ReadAsStringAsync();
                return ForgejoTestHelpers.CreateJsonResponse(new { id = 9001L });
            }

            return uri switch
            {
                "https://codeberg.example.com/api/v1/user" => ForgejoTestHelpers.CreateJsonResponse(new { login = "meister-dev" }),
                "https://codeberg.example.com/api/v1/repos/acme/propr/pulls/42/reviews?limit=100" =>
                    ForgejoTestHelpers.CreateJsonResponse(Array.Empty<object>()),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });

        var sut = new ForgejoCodeReviewPublicationService(
            new ForgejoConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory,
            TestPostedCommentComposer.Distinctive);

        await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        Assert.NotNull(postedReview);
        const string marker = TestPostedCommentComposer.DistinctiveMarker;
        using var payload = JsonDocument.Parse(postedReview);
        TestPostedCommentComposer.AssertMarkedOnce(payload.RootElement.GetProperty("body").GetString(), marker);
        var summary = payload.RootElement.GetProperty("body").GetString()!;
        Assert.Contains("Looks solid overall.", summary);
        Assert.Contains("- **Warning** · `src/file.ts:18`: Guard this null case.", summary);
        Assert.Contains(withRisk ? "| 3 | Low: 3 | Unassessed: 3 |" : "| 3 | Unassessed: 3 | Unassessed: 3 |", summary);
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
            Assert.StartsWith("![Severity: Warning]", inlineBody);
            Assert.Contains("![Risk: Low]", inlineBody);
            Assert.Contains("\n\nGuard this null case.", inlineBody);
            Assert.Contains("<details>\n<summary>Assessment details</summary>", inlineBody);
            Assert.Contains("| Risk | Low |", inlineBody);
            Assert.Contains("Limited local disruption.", inlineBody);
            Assert.Contains("| Risk | Low |", payload.RootElement.GetProperty("body").GetString());
        }
        else
        {
            Assert.StartsWith("![Severity: Warning]", inlineBody);
            Assert.Contains("\n\nGuard this null case.\n\n" + marker, inlineBody);
        }
    }
}
