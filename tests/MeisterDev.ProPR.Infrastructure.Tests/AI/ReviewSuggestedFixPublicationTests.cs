// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Security;
using MeisterDev.ProPR.Infrastructure.Tests.GitLab;

namespace MeisterDev.ProPR.Infrastructure.Tests.AI;

public sealed class ReviewSuggestedFixPublicationTests
{
    [Theory]
    [InlineData(FindingBadgeStyle.Plain)]
    [InlineData(FindingBadgeStyle.ShieldsIo)]
    public void AllProviders_OmitIdenticalFixAndRetainValidFinding(FindingBadgeStyle style)
    {
        var original = Finding();
        var finding = original with { SuggestedFix = original.SuggestedFix! with { ReplacementText = original.SuggestedFix.OriginalText } };
        var bodies = new[]
        {
            AdoCommentPoster.FormatInlineCommentBody(finding, TestPostedCommentComposer.Default, true, style),
            ForgejoFindingCommentRenderer.Format(finding, badgeStyle: style),
            GitHubFindingCommentRenderer.Format(finding, true, badgeStyle: style),
            GitLabFindingCommentRenderer.Format(finding, true, "suggestion:-0+1", style),
        };

        foreach (var body in bodies)
        {
            Assert.Contains(finding.Message, body);
            Assert.DoesNotContain("Suggested change", body);
            Assert.DoesNotContain("```suggestion", body);
            Assert.DoesNotContain("old1;", body);
        }
    }

    private const string Replacement = "return values.Where(x => x < 2 && x != \"raw\");";

    [Theory]
    [InlineData(false, FindingBadgeStyle.ShieldsIo)]
    [InlineData(true, FindingBadgeStyle.ShieldsIo)]
    [InlineData(false, FindingBadgeStyle.Plain)]
    [InlineData(true, FindingBadgeStyle.Plain)]
    public void GitHub_OverBudgetOptionalFixPreservesFindingAndOriginalAnchor(bool fileless, FindingBadgeStyle style)
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repo = new RepositoryRef(host, "1", "org", "org/repo");
        var finding = Finding().WithMessage(new string('m', 54_000));
        finding = finding with { SuggestedFix = finding.SuggestedFix! with { ReplacementText = new string('x', 12_000) } };
        if (fileless)
        {
            finding = finding.AsPullRequestLevel(finding.Message);
        }

        var payload = GitHubLifecyclePublicationService.BuildPayload(
            new CodeReviewRef(repo, CodeReviewPlatformKind.PullRequest, "1", 1), new ReviewRevision("head", "base", null, null, null),
            new ReviewResult("Narrative.", [finding]), new ReviewerIdentity(host, "bot", "bot", "Bot", true), TestPostedCommentComposer.Default, "head", style);

        var body = fileless ? payload.Body : Assert.Single(payload.Comments).Body;
        Assert.InRange(body.Length, 1, 65_536);
        Assert.Contains(finding.Message, body);
        Assert.EndsWith(TestPostedCommentComposer.Default.Text, body);
        Assert.DoesNotContain("```suggestion", body);
        Assert.DoesNotContain(finding.SuggestedFix.ReplacementText, body);
        Assert.DoesNotContain("Suggested change", body);
        Assert.DoesNotContain("propr-suggested-fix:start", body);
        Assert.Equal(style == FindingBadgeStyle.ShieldsIo, body.Contains("img.shields.io", StringComparison.Ordinal));
        if (!fileless)
        {
            var inline = Assert.Single(payload.Comments);
            Assert.Equal(finding.LineNumber, inline.Line);
            Assert.Null(inline.StartLine);
        }

        Assert.Equal(12_000, finding.SuggestedFix.ReplacementText.Length);
    }

    [Theory]
    [InlineData("head", true, FindingBadgeStyle.ShieldsIo)]
    [InlineData("new-head", false, FindingBadgeStyle.ShieldsIo)]
    [InlineData("head", true, FindingBadgeStyle.Plain)]
    [InlineData("new-head", false, FindingBadgeStyle.Plain)]
    public void GitHub_PairsValidatedCodeWithExactRightSideRange(string head, bool applicable, FindingBadgeStyle style)
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repo = new RepositoryRef(host, "1", "org", "org/repo");
        var finding = Finding();
        var payload = GitHubLifecyclePublicationService.BuildPayload(
            new CodeReviewRef(repo, CodeReviewPlatformKind.PullRequest, "1", 1), new ReviewRevision(head, "base", null, null, null),
            new ReviewResult("Narrative.", [finding]), new ReviewerIdentity(host, "bot", "bot", "Bot", true), TestPostedCommentComposer.Default, head, style);
        var inline = Assert.Single(payload.Comments);
        Assert.Equal(applicable ? 3 : 2, inline.Line);
        Assert.Equal(applicable ? 2 : null, inline.StartLine);
        Assert.Equal(applicable ? "RIGHT" : null, inline.StartSide);
        Assert.Equal(applicable, inline.Body.Contains("```suggestion", StringComparison.Ordinal));
        Assert.Contains(applicable ? "```suggestion\n" : "**Suggested change**\n\n```csharp\n", inline.Body);
        Assert.Equal(!applicable, inline.Body.Contains("**Suggested change**", StringComparison.Ordinal));
        Assert.Contains(Replacement, inline.Body);
        Assert.Equal(style == FindingBadgeStyle.ShieldsIo, inline.Body.Contains("img.shields.io", StringComparison.Ordinal));
        Assert.DoesNotContain("Highest assessed priority", inline.Body);
        Assert.DoesNotContain("Fix first", inline.Body);
        Assert.Contains("**Fix first · Risk: Critical** · `a.cs:2`: Exact input loses data.", payload.Body);
        Assert.True(payload.Body.IndexOf("Fix first", StringComparison.Ordinal) < payload.Body.IndexOf("Narrative.", StringComparison.Ordinal));
        Assert.Equal(
            "ERROR: Exact input loses data.",
            ReviewFindingTextFormatter.StripAssessment(inline.Body.Split("\n\n" + TestPostedCommentComposer.Default.Text, StringSplitOptions.None)[0]));
    }

    [Theory]
    [InlineData("head", true, FindingBadgeStyle.ShieldsIo)]
    [InlineData("new-head", false, FindingBadgeStyle.ShieldsIo)]
    [InlineData("head", true, FindingBadgeStyle.Plain)]
    [InlineData("new-head", false, FindingBadgeStyle.Plain)]
    public void Azure_UsesExactWholeSourceRangeOnlyForMatchingRevision(string head, bool applicable, FindingBadgeStyle style)
    {
        var finding = Finding();
        var anchor = AdoCommentPoster.ResolveAnchorContext(finding, 1, null, new Dictionary<string, int> { ["/a.cs"] = 1 }, head);
        var (thread, _) = AdoCommentPoster.BuildThreadContexts(anchor);
        Assert.Equal(2, thread!.RightFileStart.Line);
        Assert.Equal(applicable ? 3 : 2, thread.RightFileEnd.Line);
        Assert.Equal(applicable ? 6 : 1, thread.RightFileEnd.Offset);
        var body = AdoCommentPoster.FormatInlineCommentBody(finding, TestPostedCommentComposer.Default, applicable, style);
        Assert.Equal(applicable, body.Contains("```suggestion", StringComparison.Ordinal));
        Assert.Contains(applicable ? "```suggestion\n" : "**Suggested change**\n\n```csharp\n", body);
        Assert.Equal(!applicable, body.Contains("**Suggested change**", StringComparison.Ordinal));
        Assert.Contains(Replacement, body);
        Assert.Equal(style == FindingBadgeStyle.ShieldsIo, body.Contains("img.shields.io", StringComparison.Ordinal));
        Assert.DoesNotContain("Highest assessed priority", body);
        Assert.DoesNotContain("Fix first", body);
        var fallback = AdoCommentPoster.ResolveAnchorContext(finding, 1, null, new Dictionary<string, int>(), head);
        Assert.Null(AdoCommentPoster.BuildThreadContexts(fallback).ThreadContext!.RightFileStart);
    }

    [Theory]
    [InlineData(FindingBadgeStyle.ShieldsIo)]
    [InlineData(FindingBadgeStyle.Plain)]
    public void Forgejo_DisplaysReplacementAsOrdinaryCode(FindingBadgeStyle style)
    {
        var body = ForgejoFindingCommentRenderer.Format(Finding(), badgeStyle: style);
        Assert.DoesNotContain("```suggestion", body);
        Assert.Contains("**Suggested change**\n\n```csharp\n" + Replacement + "\n```", body);
        Assert.Contains(Replacement, body);
        Assert.Equal(style == FindingBadgeStyle.ShieldsIo, body.Contains("img.shields.io", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("head", "head", true, FindingBadgeStyle.ShieldsIo)]
    [InlineData("new-head", "new-head", false, FindingBadgeStyle.ShieldsIo)]
    [InlineData("head", "new-head", false, FindingBadgeStyle.ShieldsIo)]
    [InlineData("head", null, false, FindingBadgeStyle.ShieldsIo)]
    [InlineData("head", "head", true, FindingBadgeStyle.Plain)]
    [InlineData("new-head", "new-head", false, FindingBadgeStyle.Plain)]
    [InlineData("head", "new-head", false, FindingBadgeStyle.Plain)]
    [InlineData("head", null, false, FindingBadgeStyle.Plain)]
    public async Task GitLab_PostsExactRelativeRangeWithoutEncodingReplacement(string liveHead, string? currentHead, bool applicable, FindingBadgeStyle style)
    {
        var id = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var repo = new RepositoryRef(host, "101", "acme", "acme/repo");
        var bodies = new List<string>();
        var connections = GitLabTestHelpers.CreateConnectionRepository(id, host);
        var http = GitLabTestHelpers.CreateHttpClientFactory(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/discussions", StringComparison.Ordinal))
            {
                bodies.Add(await request.Content!.ReadAsStringAsync());
                return GitLabTestHelpers.CreateJsonResponse(new { id = "discussion" }, HttpStatusCode.Created);
            }

            return path.EndsWith("/user", StringComparison.Ordinal)
                ? GitLabTestHelpers.CreateJsonResponse(new { username = "bot" })
                : GitLabTestHelpers.CreateJsonResponse(
                    new[] { new { id = 1L, base_commit_sha = "base", head_commit_sha = liveHead, start_commit_sha = "start" } });
        });
        var review = new CodeReviewRef(repo, CodeReviewPlatformKind.PullRequest, "1", 1);
        var revision = new ReviewRevision("head", "base", "start", null, null);
        var reviewer = new ReviewerIdentity(host, "bot", "bot", "Bot", true);
        await new GitLabCodeReviewPublicationService(new GitLabConnectionVerifier(connections, http), http, TestPostedCommentComposer.Default)
            .PublishReviewAsync(
                id, review, revision, new ReviewResult("Narrative.", [Finding()]), reviewer,
                publicationContext: new ReviewPublicationContext(review, revision, reviewer, []) { CurrentHeadSha = currentHead, FindingBadgeStyle = style });
        var inline = bodies.Single(body => body.Contains("position[new_line]", StringComparison.Ordinal));
        Assert.Equal("2", GitLabTestHelpers.ReadMultipartField(inline, "position[new_line]"));
        var content = GitLabTestHelpers.ReadMultipartField(inline, "body");
        Assert.Equal(applicable, content.Contains("```suggestion:-0+1", StringComparison.Ordinal));
        Assert.Contains(applicable ? "```suggestion:-0+1\n" : "**Suggested change**\n\n```csharp\n", content);
        Assert.Equal(!applicable, content.Contains("**Suggested change**", StringComparison.Ordinal));
        Assert.Contains(Replacement, content);
        Assert.Equal(style == FindingBadgeStyle.ShieldsIo, content.Contains("img.shields.io", StringComparison.Ordinal));
        Assert.DoesNotContain("&lt;", content);
        var header = content.Split("\n\n", StringSplitOptions.None)[0];
        var positions = new[] { "Severity: Error", "Risk: Critical", "Impact: Critical", "Likelihood: Likely", "Confidence: Unassessed", "Type: Logic error" }
            .Select(label => header.IndexOf(label, StringComparison.Ordinal)).ToArray();
        Assert.All(positions, position => Assert.True(position >= 0));
        Assert.Equal(positions.Order(), positions);
        Assert.DoesNotContain("Highest assessed priority", content);
        Assert.DoesNotContain("Fix first", content);
        var summary = GitLabTestHelpers.ReadMultipartField(bodies.Single(body => !body.Contains("position[new_line]", StringComparison.Ordinal)), "body");
        Assert.Contains("**Fix first · Risk: Critical** · `a.cs:2`: Exact input loses data.", summary);
        Assert.True(summary.IndexOf("Fix first", StringComparison.Ordinal) < summary.IndexOf("Narrative.", StringComparison.Ordinal));
    }

    private static ReviewComment Finding() => ReviewSuggestedFixValidator.Bind(
        new ReviewComment("a.cs", 2, CommentSeverity.Error, "Exact input loses data.")
        {
            SuggestedFix = new ReviewSuggestedFix(2, 3, "old1;\nold2;", Replacement),
            RiskAssessment = new FindingRiskAssessment(
                FindingImpact.Critical, FindingLikelihood.Likely, "Data loss.", "On input.", "review_model", FindingRiskAssessment.CurrentRubricVersion),
            FindingClassification = new(
                [FindingTypeTaxonomy.LogicError], [], null, null, null, FindingTypeTaxonomy.Version, FindingTypeClassification.ReviewSource),
        }, [new ChangedFile("a.cs", ChangeType.Edit, "before;\nold1;\nold2;", "@@ -1,1 +1,3 @@\n+before;\n+old1;\n+old2;")], "head");
}
