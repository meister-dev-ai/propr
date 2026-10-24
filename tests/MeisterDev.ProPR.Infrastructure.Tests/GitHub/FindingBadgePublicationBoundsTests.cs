// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Markdig;
using Markdig.Syntax;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;

namespace MeisterDev.ProPR.Infrastructure.Tests.GitHub;

public sealed class FindingBadgePublicationBoundsTests
{
    private const string Replacement = "return values.Where(value => value < 2 && value != \"raw\");\r\n";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void OversizedUnicodeMetadataKeepsTheClaimFixAndComposedFooter(bool fileless, bool withFix)
    {
        var finding = Finding("Leaks tenant data.", withFix);
        if (fileless)
        {
            finding = finding.AsPullRequestLevel(finding.Message);
        }

        var composer = TestPostedCommentComposer.With(new string('m', 700) + " <signature>");
        var payload = Payload([finding], composer);
        var body = fileless ? payload.Body : Assert.Single(payload.Comments).Body;

        Assert.InRange(body.Length, 1, 65_536);
        Assert.Contains(finding.Message, body);
        Assert.Contains("Data loss.", body);
        Assert.Contains("img.shields.io", body);
        Assert.DoesNotContain('\uFFFD', body);
        var marker = GitHubFindingCommentRenderer.RenderText(composer.Text);
        TestPostedCommentComposer.AssertMarkedOnce(body, marker);
        Assert.All(
            body.Split('\n').Where(line => line.StartsWith("![Severity:", StringComparison.Ordinal)), header => Assert.EndsWith("?style=flat-square)", header));
        if (fileless)
        {
            Assert.StartsWith("## Review Bot Review", body);
        }
        else
        {
            var inline = Assert.Single(payload.Comments);
            Assert.Equal(withFix ? 3 : 2, inline.Line);
            Assert.Equal(withFix ? 2 : null, inline.StartLine);
            Assert.Equal(withFix, body.Contains("```suggestion", StringComparison.Ordinal));
            Assert.Equal(
                "ERROR: " + finding.Message, ReviewFindingTextFormatter.StripAssessment(composer.StripRendered(body, GitHubFindingCommentRenderer.RenderText)));
        }

        if (withFix)
        {
            Assert.Contains(Replacement, body);
            Assert.Equal(Replacement, finding.SuggestedFix!.ReplacementText);
        }
    }

    [Theory]
    [InlineData(FindingBadgeStyle.ShieldsIo, 3)]
    [InlineData(FindingBadgeStyle.Plain, 10)]
    public void SeveralFilelessFindingsShareTheBudgetWithoutRewritingLiteralBadgeExamples(FindingBadgeStyle style, int count)
    {
        var literalHeader = GitHubFindingCommentRenderer.Format(new ReviewComment("a.cs", 1, CommentSeverity.Info, "Example."), badgeStyle: style)
            .Split("\n\n", StringSplitOptions.None)[0];
        var example = "```markdown\n" + literalHeader + "\n\nLiteral badge example.\n```";
        var findings = Enumerable.Range(0, count).Select(index => Finding($"Tenant {index} loses data.\n\n{example}", false)
            .AsPullRequestLevel($"Tenant {index} loses data.\n\n{example}")).ToArray();
        var composer = TestPostedCommentComposer.Distinctive;
        var payload = Payload(findings, composer, style);

        Assert.InRange(payload.Body.Length, 1, 65_536);
        Assert.StartsWith("## Review Bot Review", payload.Body);
        Assert.Empty(payload.Comments);
        foreach (var finding in findings)
        {
            Assert.Contains(finding.Message, payload.Body);
            Assert.Contains(example, payload.Body);
            Assert.Equal(64, finding.FindingClassification!.CoreSlugs.Count + finding.FindingClassification.CustomTags.Count);
        }

        Assert.Equal(count, payload.Body.Split("<details>", StringSplitOptions.None).Length - 1);
        Assert.Equal(count, payload.Body.Split("</details>", StringSplitOptions.None).Length - 1);
        Assert.Contains(style == FindingBadgeStyle.Plain ? "`Severity: Error`" : "![Severity: Error]", payload.Body);
        TestPostedCommentComposer.AssertMarkedOnce(payload.Body, TestPostedCommentComposer.DistinctiveMarker);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public void OversizedSummaryShellRetainsFilelessFindingsAndClosedMarkdown(int count, bool oversizedContext)
    {
        var findings = Enumerable.Range(0, count).Select(index => new ReviewComment(
                null, null, CommentSeverity.Error, $"Tenant {index} loses data.\n\n```csharp\nreturn value;\n```"))
            .ToArray();
        var summary = "```text\n" + new string('x', 70_000);
        var skipped = oversizedContext ? new[] { "src/" + new string('y', 70_000) + ".cs" } : [];

        var payload = Payload(findings, TestPostedCommentComposer.Distinctive, summary: summary, skippedPaths: skipped);

        Assert.InRange(payload.Body.Length, 1, 65_536);
        Assert.Empty(payload.Comments);
        foreach (var finding in findings)
        {
            Assert.Contains(finding.Message, payload.Body);
        }

        var code = Markdown.Parse(payload.Body).Descendants().OfType<FencedCodeBlock>().ToArray();
        Assert.InRange(code.Length, count, count + 1);
        Assert.All(code, block => Assert.True(block.ClosingFencedCharCount >= 3));
        TestPostedCommentComposer.AssertMarkedOnce(payload.Body, TestPostedCommentComposer.DistinctiveMarker);
        Assert.Contains("<em>" + TestPostedCommentComposer.DistinctiveWording + "</em>", Markdown.ToHtml(payload.Body));
        Assert.Equal(70_008, summary.Length);
        if (oversizedContext)
        {
            Assert.Contains("not reviewed)", payload.Body);
            Assert.Equal(70_007, Assert.Single(skipped).Length);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void OversizedFilelessLocationsKeepFindingDetailsWithinTheProviderLimit(int count)
    {
        var path = "src/" + new string('y', 70_000) + ".cs";
        var findings = Enumerable.Range(0, count)
            .Select(index => new ReviewComment(path, null, CommentSeverity.Warning, $"Finding {index} loses data."))
            .ToArray();

        var payload = Payload(findings, TestPostedCommentComposer.Distinctive);

        Assert.InRange(payload.Body.Length, 1, 65_536);
        Assert.Empty(payload.Comments);
        foreach (var finding in findings)
        {
            Assert.Contains(finding.Message, payload.Body);
            Assert.Equal(path, finding.FilePath);
        }

        Assert.Contains("src/" + new string('y', 156) + "…", payload.Body);
        TestPostedCommentComposer.AssertMarkedOnce(payload.Body, TestPostedCommentComposer.DistinctiveMarker);
    }

    private static GitHubLifecyclePublicationService.GitHubReviewRequest Payload(
        IReadOnlyList<ReviewComment> findings, MeisterDev.ProPR.Application.Interfaces.IPostedCommentComposer composer,
        FindingBadgeStyle style = FindingBadgeStyle.ShieldsIo, string summary = "Conclusion.", IReadOnlyList<string>? skippedPaths = null)
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "1", "org", "org/repo");
        return GitHubLifecyclePublicationService.BuildPayload(
            new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "1", 1), new ReviewRevision("head", "base", null, null, null),
            new ReviewResult(summary, findings) { ContextSkippedFilePaths = skippedPaths ?? [] },
            new ReviewerIdentity(host, "bot", "bot", "Review Bot", true), composer, "head", style);
    }

    private static ReviewComment Finding(string message, bool withFix)
    {
        var finding = new ReviewComment("a.cs", 2, CommentSeverity.Error, message)
        {
            FindingClassification = new(
                ["logic-error"], Enumerable.Range(0, 63)
                    .Select(index => new FindingCustomTypeAssignment(Guid.NewGuid(), $"custom-{index}", new string('界', 126) + $"{index:00}")).ToArray(),
                null, null, null, FindingTypeTaxonomy.Version, FindingTypeClassification.ReviewSource),
            RiskAssessment = new(
                FindingImpact.Critical, FindingLikelihood.Likely, "Data loss.", "Every request.", "review_model",
                FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.Certain, "The complete path is visible."),
            SuggestedFix = withFix ? new ReviewSuggestedFix(2, 3, "old1;\nold2;", Replacement) : null,
        };
        return ReviewSuggestedFixValidator.Bind(
            finding,
            [new ChangedFile("a.cs", ChangeType.Edit, "before;\nold1;\nold2;", "@@ -1,1 +1,3 @@\n+before;\n+old1;\n+old2;")], "head");
    }
}
