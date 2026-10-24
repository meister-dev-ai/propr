// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using Markdig;
using Markdig.Extensions.Tables;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Tests.Services;

public sealed class FindingRiskPresentationTests
{
    [Theory]
    [InlineData("<script></script>\n    <details>Hidden</details>")]
    [InlineData("| A | B |\n| - | - |\n| ` | <details>Hidden</details>` |")]
    [InlineData("`\n<details>Hidden</details>\n`")]
    [InlineData("<div>\n```\n\n```\n`\n<details>Hidden</details>\n`\n```")]
    public void PublicationText_UsesPostNeutralizationCodeContexts(string message)
    {
        var body = ReviewFindingTextFormatter.Format(Comment().AsPullRequestLevel(message), collapseAssessment: true);
        var html = Markdown.ToHtml(body, new MarkdownPipelineBuilder().UsePipeTables(new PipeTableOptions { UseGfmRules = true }).Build());

        Assert.DoesNotContain("<details>Hidden</details>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<details>Hidden</details>", body, StringComparison.Ordinal);
        Assert.Contains("&lt;", html, StringComparison.Ordinal);
        Assert.Equal("ERROR: " + message, ReviewFindingTextFormatter.StripAssessment(body));
    }

    [Theory]
    [InlineData("```")]
    [InlineData("~~~csharp\nTask<T> result;")]
    [InlineData("<div>\n```\nTask<T> result;")]
    [InlineData("Literal <!-- propr-fence-repair -->.\n```")]
    public void PublicationText_RepairsOpenTopLevelFencesWithoutChangingRawIdentity(string message)
    {
        var finding = Comment().AsPullRequestLevel(message);

        var body = ReviewFindingTextFormatter.Format(finding, collapseAssessment: true);

        Assert.Contains("<!-- propr-fence-repair -->\n\n<!-- propr-risk-assessment:start -->", body, StringComparison.Ordinal);
        Assert.Equal("ERROR: " + message, ReviewFindingTextFormatter.StripAssessment(body));
        Assert.Equal(message, finding.Message);
        Assert.Contains("<details>", Markdig.Markdown.ToHtml(body), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("```csharp\nTask<T> result;")]
    [InlineData("~~~csharp\nTask<T> result;")]
    public void PublicationText_OpenCodeFenceRetainsCopiedCodeAfterRepair(string message)
    {
        var body = ReviewFindingTextFormatter.Format(Comment().AsPullRequestLevel(message), collapseAssessment: true);

        Assert.Contains("Task&lt;T&gt; result;", Markdig.Markdown.ToHtml(body), StringComparison.Ordinal);
        Assert.DoesNotContain('\u200B', body);
        Assert.Equal("ERROR: " + message, ReviewFindingTextFormatter.StripAssessment(body));
    }

    [Theory]
    [InlineData("> ```\n> Task<T> result;")]
    [InlineData("- ```\n  Task<T> result;")]
    [InlineData("```\nTask<T> result;\n````")]
    public void PublicationText_DoesNotAppendAnUnprefixedFenceForClosedOrNestedBlocks(string message)
    {
        var body = ReviewFindingTextFormatter.Format(Comment().AsPullRequestLevel(message), collapseAssessment: true);

        Assert.DoesNotContain("<!-- propr-fence-repair -->", body, StringComparison.Ordinal);
        Assert.Equal("ERROR: " + message, ReviewFindingTextFormatter.StripAssessment(body));
        Assert.Contains("<details>", Markdig.Markdown.ToHtml(body), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void IdentityNormalization_RecognizesCanonicalHtmlAssessments(string newline)
    {
        Assert.Equal(
            "ERROR: Unscoped delete.", ReviewFindingTextFormatter.StripAssessment(HtmlAssessmentBody().Replace("\n", newline, StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("Use `Task<T>` and **strong** or *emphasis*.")]
    [InlineData("Copyright &copy; and nonbreaking &nbsp; text.")]
    [InlineData("[reference](https://example.com/x?a=1&b=2) and ![chart](https://example.com/chart.png)")]
    [InlineData("[unsafe](javascript:alert) and ![unsafe](data:image/svg+xml,bad)")]
    [InlineData("<details>Literal HTML</details> and a\nbreak.")]
    public void IdentityNormalization_RecognizesGeneratedHtmlInlineCells(string reason)
    {
        var finding = Comment() with
        {
            RiskAssessment = new FindingRiskAssessment(
                FindingImpact.Critical, FindingLikelihood.Likely, reason, "Every delete uses this query.", "review_model",
                FindingRiskAssessment.CurrentRubricVersion),
        };

        var body = ReviewFindingTextFormatter.Format(finding, htmlAssessment: true);

        Assert.Equal("ERROR: " + finding.Message, ReviewFindingTextFormatter.StripAssessment(body));
    }

    [Theory]
    [InlineData("<table>", "<table class=\"example\">")]
    [InlineData("<td>Impact</td>", "<td>Example</td>")]
    [InlineData("<td>Deletes tenant data.</td>", "<td><script>bad</script></td>")]
    [InlineData("<td>Critical</td>", "<td>High</td>")]
    [InlineData("</table>", "")]
    [InlineData("</details>", "</details>Extra finding text.")]
    public void IdentityNormalization_PreservesNoncanonicalHtmlAssessmentLookalikes(string original, string replacement)
    {
        var body = HtmlAssessmentBody().Replace(original, replacement, StringComparison.Ordinal);
        Assert.Equal(body, ReviewFindingTextFormatter.StripAssessment(body));
    }

    [Fact]
    public void IdentityNormalization_PreservesCanonicalHtmlUsedAsALiteralExample()
    {
        var body = "Preserve this example.\n\n" + HtmlAssessmentBody();
        Assert.Equal(body, ReviewFindingTextFormatter.StripAssessment(body));
    }

    private static string HtmlAssessmentBody() =>
        "`Severity: Error` `Risk: Critical` `Impact: Critical` `Likelihood: Likely` `Confidence: Certain`\n\nUnscoped delete.\n\n"
        + "<!-- propr-risk-assessment:start -->\n\n<details><summary>Assessment details</summary><table>"
        + "<tr><th>Assessment</th><th>Level</th><th>Reason</th></tr>"
        + "<tr><td>Risk</td><td>Critical</td><td>Derived from impact and likelihood.</td></tr>"
        + "<tr><td>Impact</td><td>Critical</td><td>Deletes tenant data.</td></tr>"
        + "<tr><td>Likelihood</td><td>Likely</td><td>Every delete uses this query.</td></tr>"
        + "<tr><td>Confidence</td><td>Certain</td><td>The unscoped query includes all tenants.</td></tr>"
        + "</table></details>\n\n<!-- propr-risk-assessment:end -->";

    [Theory]
    [InlineData("Use `Task<T>` before returning.")]
    [InlineData("Use `Task<T>|other` before returning.")]
    [InlineData("| A | B |\n| - | - |\n| `Task<T>\\|other` | Safe. |")]
    [InlineData("Use `` `Task<T>` `` before returning.")]
    [InlineData("Use `Task<\nT>` before returning.")]
    [InlineData("```csharp\nTask<T> result;\n```")]
    [InlineData("```csharp\nTask<T> result;\n````")]
    [InlineData("~~~csharp\nTask<T> result;\n~~~~")]
    [InlineData("    Task<T> result;")]
    [InlineData("\tTask<T> result;")]
    [InlineData("Example:\n\n    Task<T> result;\n    </details>")]
    [InlineData("> ```csharp\n> Task<T> result;\n> ```")]
    [InlineData("Read <https://example.com/x> and contact <reviewer@example.com>.")]
    [InlineData("Read [docs](<https://example.com/x?a=1&b=2>) before returning.")]
    [InlineData("Read [docs](<guide.md>) before returning.")]
    public void DisplayText_PreservesValidCodeAndAutolinksWithoutChangingCopiedSource(string message)
    {
        Assert.Equal(message, ReviewFindingTextFormatter.RenderText(message));
        var finding = Comment().AsPullRequestLevel(message);
        var body = ReviewFindingTextFormatter.Format(finding, collapseAssessment: true);
        Assert.Contains("\n\n" + message + "\n\n<!-- propr-risk-assessment:start -->", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("````", "```", "`````")]
    [InlineData("~~~~", "~~~", "~~~~~")]
    public void DisplayText_UsesTheMatchingFenceBoundaryBeforeNeutralizingSubsequentHtml(string opening, string shorter, string closing)
    {
        var code = $"{opening}csharp\nTask<T> result;\n{shorter}\n</details>\n{closing}";

        var body = ReviewFindingTextFormatter.RenderText(code + "\n\n</details>");

        Assert.Equal(code + "\n\n<\u200B/details>", body);
    }

    [Theory]
    [InlineData("Paragraph.\n    </details>")]
    [InlineData("`Unmatched </details>")]
    [InlineData("\\`Escaped </details>\\`")]
    [InlineData("> </details>")]
    [InlineData("<https://example.com x>")]
    [InlineData("<reviewer@example.com\nonclick=alert(1)>")]
    public void DisplayText_NeutralizesHtmlOutsideRecognizedMarkdownConstructs(string message)
    {
        var body = ReviewFindingTextFormatter.RenderText(message);

        Assert.Equal(message.Replace("<", "<\u200B", StringComparison.Ordinal), body);
    }

    [Fact]
    public void PublicationText_PreservesDescriptionCodeWhileNeutralizingCodeLikeTextInsideAssessmentCells()
    {
        const string description = "```csharp\nTask<T> result;\n</details>\n```";
        var finding = Comment().AsPullRequestLevel(description) with
        {
            RiskAssessment = new FindingRiskAssessment(
                FindingImpact.High, FindingLikelihood.Possible, "```csharp </details><script>bad</script>", "On retry.",
                "review_model", FindingRiskAssessment.CurrentRubricVersion),
        };

        var body = ReviewFindingTextFormatter.Format(finding, collapseAssessment: true);

        Assert.Contains("\n\n" + description + "\n\n<!-- propr-risk-assessment:start -->", body, StringComparison.Ordinal);
        Assert.Contains("| Impact | High | ```csharp <\u200B/details><\u200Bscript>bad<\u200B/script> |", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", body, StringComparison.Ordinal);
    }

    [Fact]
    public void PublicationText_ShowsAssessedRiskAndReasonsWithoutChangingRawMessage()
    {
        var comment = Comment();
        var body = ReviewFindingTextFormatter.Format(comment);

        Assert.StartsWith("![Severity: Error](https://img.shields.io/badge/Severity-Error-64748b?style=flat-square)", body);
        Assert.Contains("\n\n" + comment.Message + "\n\n", body);
        Assert.DoesNotContain("ERROR:", body);
        Assert.DoesNotContain("<details>", body);
        Assert.Contains("| Assessment | Level | Reason |", body);
        Assert.Contains("<!-- propr-risk-assessment:start -->\n\n| Assessment | Level | Reason |", body);
        Assert.Contains("The unscoped query includes all tenants. |\n\n<!-- propr-risk-assessment:end -->", body);
        Assert.Contains("| Risk | Critical | Derived from impact and likelihood. |", body);
        Assert.Contains("| Impact | Critical | Deletes tenant data. |", body);
        Assert.Contains("| Likelihood | Likely | Every delete uses this query. |", body);
        Assert.Contains("| Confidence | Certain | The unscoped query includes all tenants. |", body);
        Assert.Equal(6, body.Split('\n').Count(line => line.StartsWith("|", StringComparison.Ordinal)));
        Assert.Contains("The unscoped query includes all tenants.", body);
        Assert.Contains("Deletes tenant data.", body);
        Assert.Contains("Every delete uses this query.", body);
        Assert.DoesNotContain("review_model", body);
        Assert.DoesNotContain("finding-risk-v1", body);
        Assert.Equal("ERROR: " + comment.Message, ReviewFindingTextFormatter.StripAssessment(body));
        Assert.Equal("Unscoped delete.", comment.Message);
    }

    [Fact]
    public void PublicationText_ShowsUnassessedLevelsAndKeepsTheSeverityBadgeWithoutAnAssessment()
    {
        var comment = Comment() with
        {
            RiskAssessment = new FindingRiskAssessment(
                FindingImpact.High, FindingLikelihood.NotApplicable, "Increases maintenance effort.", "No occurrence scenario is supported.",
                "review_model", FindingRiskAssessment.CurrentRubricVersion)
        };
        Assert.Contains("| Risk | Unassessed |", ReviewFindingTextFormatter.Format(comment));
        Assert.Contains("| Likelihood | Not applicable |", ReviewFindingTextFormatter.Format(comment));
        Assert.Contains("| Confidence | Unassessed | Not assessed. |", ReviewFindingTextFormatter.Format(comment));
        Assert.Contains("| Impact | High |", ReviewFindingTextFormatter.Format(comment));
        Assert.StartsWith("![Severity: Error]", ReviewFindingTextFormatter.Format(comment with { RiskAssessment = null }));
    }

    [Fact]
    public void PublicationText_UnrecognizedClassificationUsesTheInfoLabel()
    {
        var comment = Comment().WithAnchorAndSeverity("src/Work.cs", 7, (CommentSeverity)99) with { RiskAssessment = null };

        Assert.StartsWith("![Severity: Info]", ReviewFindingTextFormatter.Format(comment));
    }

    [Theory]
    [InlineData("\n", false)]
    [InlineData("\r\n", false)]
    [InlineData("\n", true)]
    [InlineData("\r\n", true)]
    public void IdentityNormalization_RecognizesCompleteAssessments(string newline, bool collapsed)
    {
        var body = AssessedBody(collapsed).Replace("\n", newline, StringComparison.Ordinal);

        Assert.Equal("ERROR: Unscoped delete.", ReviewFindingTextFormatter.StripAssessment(body));
    }

    [Theory]
    [InlineData("Error", "ERROR")]
    [InlineData("Warning", "WARNING")]
    [InlineData("Suggestion", "SUGGESTION")]
    [InlineData("Info", "INFO")]
    public void IdentityNormalization_RecognizesClassificationWithoutAnAssessment(string classification, string prefix)
    {
        Assert.Equal(prefix + ": Unscoped delete.", ReviewFindingTextFormatter.StripAssessment($"`Severity: {classification}`\n\nUnscoped delete."));
    }

    [Theory]
    [InlineData("`Risk: Critical`", "`Risk: High`")]
    [InlineData("Impact: Critical", "Impact: High")]
    [InlineData("Likelihood: Likely", "Likelihood: Possible")]
    [InlineData("Confidence: Certain", "Confidence: High")]
    [InlineData("</details>\n", "")]
    [InlineData("<summary>Assessment details</summary>", "<summary>Example</summary>")]
    [InlineData("<!-- propr-risk-assessment:end -->", "")]
    public void IdentityNormalization_PreservesMismatchedOrIncompleteAssessedBodies(string original, string replacement)
    {
        var body = AssessedBody(true).Replace(original, replacement, StringComparison.Ordinal);

        Assert.Equal(body, ReviewFindingTextFormatter.StripAssessment(body));
    }

    [Fact]
    public void IdentityNormalization_PreservesQuotedExamplesAndTrailingFindingText()
    {
        var example = "> " + AssessedBody(true).Replace("\n", "\n> ", StringComparison.Ordinal);
        Assert.Equal(example, ReviewFindingTextFormatter.StripAssessment(example));
        var extended = AssessedBody(true) + "\nAdditional original text.";
        Assert.Equal(extended, ReviewFindingTextFormatter.StripAssessment(extended));
    }

    private static string AssessedBody(bool collapsed)
    {
        const string table = "| Assessment | Level | Reason |\n| --- | --- | --- |\n"
                             + "| Risk | Critical | Derived from impact and likelihood. |\n"
                             + "| Impact | Critical | Deletes tenant data. |\n"
                             + "| Likelihood | Likely | Every delete uses this query. |\n"
                             + "| Confidence | Certain | The unscoped query includes all tenants. |";
        return "`Severity: Error` `Risk: Critical` `Impact: Critical` `Likelihood: Likely` `Confidence: Certain`\n\nUnscoped delete.\n\n"
               + "<!-- propr-risk-assessment:start -->\n\n"
               + (collapsed ? "<details>\n<summary>Assessment details</summary>\n\n" : string.Empty)
               + table + (collapsed ? "\n\n</details>" : string.Empty)
               + "\n\n<!-- propr-risk-assessment:end -->";
    }

    [Fact]
    public void PublicationText_PreservesMarkdownCodeAndNeutralizesHtmlInReasons()
    {
        var comment = Comment() with
        {
            RiskAssessment = new FindingRiskAssessment(
                FindingImpact.High, FindingLikelihood.Possible,
                "The check `count < 0 && value > 1` rejects valid input.",
                "The payload contains <script>alert('x')</script> on every request.",
                "review_model", FindingRiskAssessment.CurrentRubricVersion),
        };

        var body = ReviewFindingTextFormatter.Format(comment);

        Assert.Contains("`count < 0 && value > 1`", body);
        Assert.DoesNotContain("<script>", body, StringComparison.Ordinal);
        Assert.DoesNotContain("</script>", body, StringComparison.Ordinal);
        Assert.Contains("<\u200Bscript>", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("</details\n>")]
    [InlineData("<script\n>alert('x')</script>")]
    [InlineData("</details")]
    public void PublicationText_NeutralizesMultilineAndUnterminatedTagsBeforeAddingDetails(string unsafeText)
    {
        var comment = Comment().AsPullRequestLevel("Guard the service. " + unsafeText);

        var body = ReviewFindingTextFormatter.Format(comment, collapseAssessment: true);

        var description = body[..body.IndexOf("<!-- propr-risk-assessment:start -->", StringComparison.Ordinal)];
        Assert.Contains("Guard the service. <\u200B", description, StringComparison.Ordinal);
        Assert.DoesNotContain(unsafeText, description, StringComparison.Ordinal);
        Assert.Equal(1, body.Split("<details>", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, body.Split("</details>", StringSplitOptions.None).Length - 1);
        Assert.Equal("ERROR: " + comment.Message, ReviewFindingTextFormatter.StripAssessment(body));
    }

    [Fact]
    public void IdentityNormalization_KeepsLiteralMarkerExamplesInsideFindingText()
    {
        const string message = "Remove this literal: <!-- propr-risk-assessment:start -->example<!-- propr-risk-assessment:end -->\nThen validate.";
        Assert.Equal(message, ReviewFindingTextFormatter.StripAssessment(message));
        var comment = new ReviewComment(null, null, CommentSeverity.Warning, message) { RiskAssessment = Comment().RiskAssessment };
        Assert.Equal("WARNING: " + message, ReviewFindingTextFormatter.StripAssessment(ReviewFindingTextFormatter.Format(comment)));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void IdentityNormalization_RemovesGeneratedAssessmentAcrossProviderLineEndings(string newline)
    {
        var comment = Comment();
        var remoteBody = ReviewFindingTextFormatter.Format(comment).Replace("\n", newline, StringComparison.Ordinal);

        Assert.Equal("ERROR: " + comment.Message, ReviewFindingTextFormatter.StripAssessment(remoteBody));
    }

    [Theory]
    [InlineData("left|right", "left\\|right")]
    [InlineData("left\\|right", "left\\|right")]
    [InlineData("left\\\\|right", "left\\\\\\|right")]
    [InlineData("`left|right`", "`left\\|right`")]
    [InlineData("`left\\|right`", "`left\\\\|right`")]
    public void PublicationText_EscapesTableDelimitersWithoutUnescapingExistingPipes(string reason, string expected)
    {
        var comment = Comment() with
        {
            RiskAssessment = new FindingRiskAssessment(
                FindingImpact.High, FindingLikelihood.Possible, reason, reason, "review_model",
                FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.High, reason),
        };

        var body = ReviewFindingTextFormatter.Format(comment);

        Assert.Contains($"| Impact | High | {expected} |", body);
        Assert.Contains($"| Likelihood | Possible | {expected} |", body);
        Assert.Contains($"| Confidence | High | {expected} |", body);
        Assert.Equal(reason, comment.RiskAssessment.ImpactReason);
        Assert.Equal("ERROR: " + comment.Message, ReviewFindingTextFormatter.StripAssessment(body));
    }

    [Theory]
    [InlineData(0, "`")]
    [InlineData(1, "`")]
    [InlineData(2, "`")]
    [InlineData(3, "`")]
    [InlineData(0, "``")]
    [InlineData(1, "``")]
    [InlineData(2, "``")]
    [InlineData(3, "``")]
    [InlineData(0, "```")]
    [InlineData(1, "```")]
    [InlineData(2, "```")]
    [InlineData(3, "```")]
    public void PublicationText_CodePipesRetainEveryLiteralBackslash(int backslashes, string delimiter)
    {
        var reason = delimiter + "a" + new string('\\', backslashes) + "|b" + delimiter;
        var expected = delimiter + "a" + new string('\\', backslashes + 1) + "|b" + delimiter;
        var comment = Comment() with
        {
            RiskAssessment = new FindingRiskAssessment(
                FindingImpact.High, FindingLikelihood.Possible, reason, reason,
                "review_model", FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.High, reason),
        };

        var body = ReviewFindingTextFormatter.Format(comment);

        Assert.Contains($"| Impact | High | {expected} |", body);
        Assert.Contains($"| Likelihood | Possible | {expected} |", body);
        Assert.Contains($"| Confidence | High | {expected} |", body);
        Assert.Equal("ERROR: " + comment.Message, ReviewFindingTextFormatter.StripAssessment(body));
        Assert.Equal(reason, comment.RiskAssessment.ImpactReason);
    }

    [Theory]
    [InlineData("\\`a\\|b", "\\`a\\|b")]
    [InlineData("`a\\|b", "`a\\|b")]
    [InlineData("`a\\\\|b", "`a\\\\\\|b")]
    [InlineData("\\``a\\|b`", "\\``a\\\\|b`")]
    [InlineData("``a`x\\|b``", "``a`x\\\\|b``")]
    public void PublicationText_CodeSpanRecognitionRespectsEscapedAndUnmatchedBackticks(string reason, string expected)
    {
        var comment = Comment() with
        {
            RiskAssessment = new FindingRiskAssessment(
                FindingImpact.High, FindingLikelihood.Possible, reason, reason,
                "review_model", FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.High, reason),
        };

        var body = ReviewFindingTextFormatter.Format(comment);

        Assert.Contains($"| Impact | High | {expected} |", body);
        Assert.Equal("ERROR: " + comment.Message, ReviewFindingTextFormatter.StripAssessment(body));
    }

    [Fact]
    public void PublicationText_KeepsMultilineReasonsInTheirOriginalCells()
    {
        const string reason = "First line.\r\nSecond line.\rThird line.\n`left|right` <script>bad</script>";
        var comment = Comment() with
        {
            RiskAssessment = new FindingRiskAssessment(
                FindingImpact.Unknown, FindingLikelihood.Unknown, reason, null, "review_model",
                FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.Low, reason),
        };

        var body = ReviewFindingTextFormatter.Format(comment);

        Assert.Contains("| Impact | Unassessed | First line. Second line. Third line. `left\\|right` <\u200Bscript>bad<\u200B/script> |", body);
        Assert.Contains("| Likelihood | Unassessed | Not assessed. |", body);
        Assert.Equal(6, body.Split('\n').Count(line => line.StartsWith("|", StringComparison.Ordinal)));
        Assert.Equal(reason, comment.RiskAssessment.ImpactReason);
    }

    [Theory]
    [InlineData("**Risk: Example**")]
    [InlineData("**Risk: Critical** · Impact: Critical · Likelihood: Likely")]
    [InlineData("| Assessment | Level | Reason |\n| --- | --- | --- |\n| Risk | Critical | Incomplete. |")]
    [InlineData(
        "| Assessment | Level | Reason |\n| --- | --- | --- |\n| Risk | Critical | Derived. |\n| Impact | Critical | Invalid | extra |\n| Likelihood | Likely | Every call. |\n| Confidence | Certain | Confirmed. |")]
    [InlineData(
        "| Assessment | Level | Reason |\n| --- | --- | --- |\n| Risk | Example | Derived. |\n| Impact | Critical | Deletes data. |\n| Likelihood | Likely | Every call. |\n| Confidence | Certain | Confirmed. |")]
    public void IdentityNormalization_PreservesIncompleteOrUnrelatedMarkedSuffixes(string content)
    {
        var body = "Keep the following example.\n\n<!-- propr-risk-assessment:start -->\n" + content + "\n<!-- propr-risk-assessment:end -->";

        Assert.Equal(body, ReviewFindingTextFormatter.StripAssessment(body));
    }

    [Fact]
    public void IdentityNormalization_DoesNotRemoveAssessmentWhenMoreFindingTextFollows()
    {
        var body = ReviewFindingTextFormatter.Format(Comment()) + "\nAdditional original text.";

        Assert.Equal(body, ReviewFindingTextFormatter.StripAssessment(body));
    }

    [Fact]
    public void WithheldAndPostingDisabledEvents_KeepTheAssessment()
    {
        var comment = Comment();
        var findings = ReviewFindingPublicationMapper.BuildProducedFindings(
            [comment], [], ReviewCommentPostingDiagnosticsDto.Empty(), false, "github", [new WithheldReviewFinding(comment, "verification_withheld")]);

        Assert.Equal(2, findings.Count);
        Assert.All(findings, finding => Assert.Equal(comment.RiskAssessment, finding.RiskAssessment));
        Assert.Equal(CodeInsightPublicationState.PostingDisabled, findings[0].PublicationState);
        Assert.Equal(CodeInsightPublicationState.PolicyWithheld, findings[1].PublicationState);
    }

    [Fact]
    public void RunnerChunks_KeepAssessedPublishedAndWithheldFindingsAcrossJsonAndAssembly()
    {
        var comment = Comment();
        var result = new ReviewResult("Review.", [comment]) { WithheldFindings = [new WithheldReviewFinding(comment, "verification_withheld")] };
        var assembly = new RunnerSubmissionAssembly("submission", 2);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var chunks = new[]
        {
            new RunnerFindingsChunk("submission", 0, 2, null, [comment]),
            new RunnerFindingsChunk("submission", 1, 2, result.Summary, [], RunnerResultAnnotations.FromResult(result)),
        };
        foreach (var chunk in chunks.Reverse())
        {
            assembly.Add(JsonSerializer.Deserialize<RunnerFindingsChunk>(JsonSerializer.Serialize(chunk, options), options)!);
        }

        var restored = assembly.Build();
        Assert.Equal(comment.RiskAssessment, Assert.Single(restored.Comments).RiskAssessment);
        Assert.Equal(comment.RiskAssessment, Assert.Single(restored.WithheldFindings).Comment.RiskAssessment);
    }

    [Fact]
    public void ClassificationOnlyRewrite_PreservesAssessmentWhileMalformedEvidenceBecomesUnknown()
    {
        var original = Comment();
        var parsed = new ReviewComment(original.FilePath, original.LineNumber, CommentSeverity.Warning, original.Message);
        Assert.Equal(original.RiskAssessment, FindingRiskAssessmentParser.PreserveUnchangedEvidence(parsed, [original]).RiskAssessment);

        using var malformed = JsonDocument.Parse("42");
        parsed = parsed with { RiskAssessment = FindingRiskAssessmentParser.Parse(malformed.RootElement) };
        Assert.Equal(FindingRiskSeverity.Unknown, FindingRiskAssessmentParser.PreserveUnchangedEvidence(parsed, [original]).RiskAssessment!.Severity);
    }

    [Fact]
    public void ClassificationRewritePreservesOnlyOmittedConfidenceForAnExactUniqueClaim()
    {
        var original = Comment();
        using var raw = JsonDocument.Parse(
            """{"risk_assessment":{"impact":"critical","likelihood":"likely","impact_reason":"Deletes tenant data.","likelihood_reason":"Every delete uses this query."}}""");
        var parsed = new ReviewComment(original.FilePath, original.LineNumber, CommentSeverity.Warning, original.Message)
            { RiskAssessment = FindingRiskAssessmentParser.ParseFinding(raw.RootElement) };
        var preserved = FindingRiskAssessmentParser.PreserveUnchangedEvidence(parsed, [original], raw.RootElement);
        Assert.Equal(FindingConfidence.Certain, preserved.RiskAssessment!.Confidence);
        Assert.Equal(original.RiskAssessment!.ConfidenceReason, preserved.RiskAssessment.ConfidenceReason);
        Assert.Equal(CommentSeverity.Warning, preserved.Severity);
        Assert.Equal(
            FindingConfidence.Unknown,
            FindingRiskAssessmentParser.PreserveUnchangedEvidence(parsed, [original, original], raw.RootElement).RiskAssessment!.Confidence);
        var changed = new ReviewComment(parsed.FilePath, parsed.LineNumber, parsed.Severity, "Different claim.") { RiskAssessment = parsed.RiskAssessment };
        Assert.Equal(
            FindingConfidence.Unknown, FindingRiskAssessmentParser.PreserveUnchangedEvidence(changed, [original], raw.RootElement).RiskAssessment!.Confidence);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("99")]
    [InlineData("\"4\"")]
    [InlineData("{}")]
    public void ExplicitInvalidConfidenceDoesNotRestorePriorCertainty(string confidence)
    {
        var original = Comment();
        using var raw = JsonDocument.Parse("{\"risk_assessment\":{\"confidence\":" + confidence + "}}");
        var parsed = new ReviewComment(original.FilePath, original.LineNumber, CommentSeverity.Warning, original.Message)
            { RiskAssessment = FindingRiskAssessmentParser.ParseFinding(raw.RootElement) };
        Assert.Equal(
            FindingConfidence.Unknown, FindingRiskAssessmentParser.PreserveUnchangedEvidence(parsed, [original], raw.RootElement).RiskAssessment!.Confidence);
    }

    [Theory]
    [InlineData(FindingConfidence.Low, "Incomplete supporting evidence.")]
    [InlineData(FindingConfidence.Certain, "Independent proof of the same claim.")]
    public void ConsolidationRetainsAgreedRiskAndWeakestConfidence(FindingConfidence confidence, string confidenceReason)
    {
        var first = Comment();
        var risk = first.RiskAssessment!;
        var other = new ReviewComment("src/Other.cs", first.LineNumber, first.Severity, first.Message)
        {
            RiskAssessment = new FindingRiskAssessment(
                risk.Impact, risk.Likelihood, risk.ImpactReason, risk.LikelihoodReason, risk.Source, risk.RubricVersion, confidence, confidenceReason)
        };
        var consolidated = Assert.Single(FindingDeduplicator.Deduplicate([first, other]));
        Assert.NotNull(consolidated.RiskAssessment);
        Assert.Equal(risk.Impact, consolidated.RiskAssessment.Impact);
        Assert.Equal(risk.Likelihood, consolidated.RiskAssessment.Likelihood);
        Assert.Equal(risk.ImpactReason, consolidated.RiskAssessment.ImpactReason);
        Assert.Equal(risk.LikelihoodReason, consolidated.RiskAssessment.LikelihoodReason);
        Assert.Equal(risk.Source, consolidated.RiskAssessment.Source);
        Assert.Equal(risk.RubricVersion, consolidated.RiskAssessment.RubricVersion);
        Assert.Equal(risk.Severity, consolidated.RiskAssessment!.Severity);
        Assert.Equal(confidence, consolidated.RiskAssessment.Confidence);
        Assert.Equal("consolidated_assessment", consolidated.RiskAssessment.ConfidenceSource);
        Assert.Contains(risk.ConfidenceReason!, consolidated.RiskAssessment.ConfidenceReason!);
        Assert.Contains(confidenceReason, consolidated.RiskAssessment.ConfidenceReason!);
    }

    [Fact]
    public void CrossFileConsolidation_ConflictingAssessmentsRemainUnassessed()
    {
        var first = Comment();
        var other = new ReviewComment("src/Other.cs", 7, first.Severity, first.Message)
        {
            RiskAssessment = new FindingRiskAssessment(
                FindingImpact.Low, FindingLikelihood.Unlikely, "Limited consequence.", "Exceptional conditions.", "review_model",
                FindingRiskAssessment.CurrentRubricVersion)
        };
        Assert.Null(Assert.Single(FindingDeduplicator.Deduplicate([first, other])).RiskAssessment);
    }

    private static ReviewComment Comment() => new("src/Work.cs", 7, CommentSeverity.Error, "Unscoped delete.")
    {
        RiskAssessment = new FindingRiskAssessment(
            FindingImpact.Critical, FindingLikelihood.Likely, "Deletes tenant data.", "Every delete uses this query.", "review_model",
            FindingRiskAssessment.CurrentRubricVersion, FindingConfidence.Certain, "The unscoped query includes all tenants."),
    };
}
