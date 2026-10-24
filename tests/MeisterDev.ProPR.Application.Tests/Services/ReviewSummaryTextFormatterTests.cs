// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Tests.Services;

public sealed class ReviewSummaryTextFormatterTests
{
    [Theory]
    [InlineData(FindingImpact.Low, FindingLikelihood.Unlikely, FindingRiskSeverity.Low)]
    [InlineData(FindingImpact.Critical, FindingLikelihood.Likely, FindingRiskSeverity.Critical)]
    public void FormatLead_IdentifiesTheFirstHighestRiskDefectWithoutChangingItsClaim(
        FindingImpact impact, FindingLikelihood likelihood, FindingRiskSeverity risk)
    {
        var first = Finding("src/First.cs", 7, impact, likelihood)
            .WithAnchorAndSeverity("src/First.cs", 7, CommentSeverity.Error).WithMessage("This input may delete another tenant's row.");
        var tied = first.WithAnchorAndSeverity("src/Tied.cs", 11, CommentSeverity.Error).WithMessage("A tied defect.");
        var suggestion = Finding("src/Suggestion.cs", 3, FindingImpact.Critical, FindingLikelihood.Likely)
            .WithAnchorAndSeverity("src/Suggestion.cs", 3, CommentSeverity.Suggestion).WithMessage("A higher-risk suggestion.");
        var unassessed = new ReviewComment("src/Unknown.cs", 5, CommentSeverity.Error, "An unassessed defect.");
        var comments = new[] { unassessed, suggestion, first, tied };
        var result = new ReviewResult("Preserve the conclusion.", comments);

        var lead = ReviewFindingPriority.FormatLead(result);

        Assert.Equal($"**Fix first · Risk: {risk}** · `src/First.cs:7`: {first.Message}\n\n", lead);
        Assert.DoesNotContain(tied.Message, lead);
        Assert.DoesNotContain(suggestion.Message, lead);
        Assert.Equal("This input may delete another tenant's row.", first.Message);
        Assert.Equal("Preserve the conclusion.", result.Summary);
        Assert.Same(comments, result.Comments);
    }

    [Fact]
    public void FormatLead_SelectsTheHighestAssessedDefectAndBoundsTheFirstParagraph()
    {
        var lower = Finding("src/Lower.cs", 2, FindingImpact.Low, FindingLikelihood.Unlikely)
            .WithAnchorAndSeverity("src/Lower.cs", 2, CommentSeverity.Error);
        var highest = Finding("src/Highest.cs", 9, FindingImpact.Critical, FindingLikelihood.Likely)
            .WithAnchorAndSeverity("src/Highest.cs", 9, CommentSeverity.Error)
            .WithMessage("<script>" + new string('x', 500) + "\n\nAdditional explanation.");
        var result = new ReviewResult("Conclusion.", [lower, highest]);

        var lead = ReviewFindingPriority.FormatLead(result);

        Assert.StartsWith("**Fix first · Risk: Critical** · `src/Highest.cs:9`: <\u200Bscript>", lead);
        Assert.EndsWith("…\n\n", lead);
        Assert.DoesNotContain("Additional explanation.", lead);
        Assert.DoesNotContain(new string('x', 500), lead);
        Assert.Equal(highest.Message, result.Comments[1].Message);
    }

    [Fact]
    public void FormatLead_RequiresAnAssessedConfirmedDefect()
    {
        var unassessed = Finding("src/Unknown.cs", 5, FindingImpact.High, FindingLikelihood.NotApplicable)
            .WithAnchorAndSeverity("src/Unknown.cs", 5, CommentSeverity.Error);
        var highRiskWarning = Finding("src/Warning.cs", 3, FindingImpact.Critical, FindingLikelihood.Likely);
        var highRiskSuggestion = highRiskWarning.WithAnchorAndSeverity("src/Suggestion.cs", 7, CommentSeverity.Suggestion);

        Assert.Empty(ReviewFindingPriority.FormatLead(new ReviewResult("Conclusion.", [unassessed, highRiskWarning, highRiskSuggestion])));
        Assert.Empty(ReviewFindingPriority.FormatLead(new ReviewResult("Conclusion.", [new ReviewComment(null, null, CommentSeverity.Error, "Unassessed.")])));
        Assert.Empty(ReviewFindingPriority.FormatLead(new ReviewResult("Conclusion.", [])));
    }

    [Fact]
    public void Format_ActionsRetainStableRiskOrderingIncludingSuggestionsAndUnassessedFindings()
    {
        var lower = Finding("src/Lower.cs", 2, FindingImpact.Low, FindingLikelihood.Unlikely).WithMessage("Lower risk.");
        var first = Finding("src/First.cs", 4, FindingImpact.Critical, FindingLikelihood.Likely).WithMessage("First critical.");
        var tied = first.WithAnchorAndSeverity("src/Tied.cs", 6, CommentSeverity.Suggestion).WithMessage("Tied critical.");
        var unassessed = new ReviewComment("src/Unknown.cs", 8, CommentSeverity.Error, "Unassessed defect.");
        var comments = new[] { unassessed, lower, first, tied };

        var text = ReviewSummaryTextFormatter.Format(new ReviewResult("Conclusion.", comments));
        var positions = new[] { first, tied, lower, unassessed }.Select(comment => text.IndexOf(comment.Message, StringComparison.Ordinal)).ToArray();

        Assert.All(positions, position => Assert.True(position >= 0));
        Assert.Equal(positions.Order(), positions);
        Assert.Contains("| 4 | Critical: 2, Low: 1, Unassessed: 1 |", text);
        Assert.Equal(new[] { unassessed, lower, first, tied }, comments);
    }

    [Fact]
    public void Format_CompactActionsNeutralizeBlockCodeSyntaxFlattenedIntoInlineText()
    {
        var finding = new ReviewComment("src/Work.cs", 7, CommentSeverity.Warning, "```csharp\n</details><script>bad</script>\n```");

        var body = ReviewSummaryTextFormatter.Format(new ReviewResult("Conclusion.", [finding]));

        Assert.Contains("- **Warning** · `src/Work.cs:7`: ```csharp <\u200B/details><\u200Bscript>bad<\u200B/script> ```", body, StringComparison.Ordinal);
        Assert.DoesNotContain("</details>", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_RoutesOnlyCompleteUnanchoredFindingsThroughTheProviderRenderer()
    {
        var inline = Finding("src/Inline.cs", 3);
        var fileless = Finding(null, null).AsPullRequestLevel("Preserve the complete PR-level finding.");
        var fileLevel = Finding("src/<details>.cs", null);
        var rendered = new List<ReviewComment>();

        var body = ReviewSummaryTextFormatter.Format(
            new ReviewResult("Conclusion.", [inline, fileless, fileLevel]), includeFilelessFindingDetails: true,
            findingRenderer: comment =>
            {
                rendered.Add(comment);
                return "Provider output: " + comment.Message;
            },
            renderText: System.Net.WebUtility.HtmlEncode);

        Assert.Equal(new[] { fileless, fileLevel }, rendered);
        Assert.Contains("- **Warning** · `src/Inline.cs:3`: Check the recorded behavior.", body);
        Assert.Contains("Provider output: Preserve the complete PR-level finding.", body);
        Assert.Contains("**`src/&lt;details&gt;.cs`**", body);
        Assert.Equal(1, body.Split(fileless.Message, StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("| Assessment |", body);
        Assert.Contains("| 3 | Medium: 3 | High: 3 |", body);
    }

    [Fact]
    public void Format_ListsOfferedFindingsAndCountsIndependentAssessmentDimensions()
    {
        var comments = new[]
        {
            Finding("src/A.cs", 7, FindingImpact.Critical, FindingLikelihood.Likely, FindingConfidence.Certain),
            Finding("src/B.cs", 11, FindingImpact.Moderate, FindingLikelihood.Possible, FindingConfidence.High),
            Finding("src/C.cs", 19, FindingImpact.Low, FindingLikelihood.Unlikely, FindingConfidence.Medium),
            new ReviewComment(null, null, CommentSeverity.Info, "Evidence remains incomplete."),
        };
        var result = new ReviewResult("Keep the original conclusion.", comments)
        {
            WithheldFindings = [new WithheldReviewFinding(comments[0], "verification_withheld")],
        };

        var text = ReviewSummaryTextFormatter.Format(result);

        Assert.StartsWith("**Findings to review**", text);
        Assert.Contains("- **Warning** · `src/A.cs:7`: Check the recorded behavior.", text);
        Assert.Contains("- **Info** · PR-level: Evidence remains incomplete.", text);
        Assert.Contains("| Findings to report | Risk levels | Confidence levels |", text);
        Assert.Contains("| 4 | Critical: 1, Medium: 1, Low: 1, Unassessed: 1 | Certain: 1, High: 1, Medium: 1, Unassessed: 1 |", text);
        Assert.True(text.IndexOf("Evidence remains incomplete.", StringComparison.Ordinal) < text.IndexOf("| Findings to report", StringComparison.Ordinal));
        Assert.Equal("Keep the original conclusion.", result.Summary);
        Assert.Same(comments, result.Comments);
        Assert.DoesNotContain("Published findings", text);
        Assert.DoesNotContain("Files reviewed", text);
        Assert.DoesNotContain("Review time", text);
    }

    [Fact]
    public void Format_EmptyOfferedSetDoesNotCountWithheldFindingsOrClaimCoverage()
    {
        var result = new ReviewResult("Review not started: the file limit was exceeded.", [])
        {
            WithheldFindings = [new WithheldReviewFinding(Finding(null, null), "verification_withheld")],
        };

        var text = ReviewSummaryTextFormatter.Format(result);

        Assert.Equal("| Findings to report | Risk levels | Confidence levels |\n| --- | --- | --- |\n| 0 | None | None |", text);
        Assert.Equal("Review not started: the file limit was exceeded.", result.Summary);
    }

    [Fact]
    public void Format_UnassessedRiskDoesNotDiscardSupportedConfidence()
    {
        var finding = Finding("src/A.cs", 3, FindingImpact.High, FindingLikelihood.NotApplicable, FindingConfidence.Low);

        var text = ReviewSummaryTextFormatter.Format(new ReviewResult("Review.", [finding]));

        Assert.Contains("| 1 | Unassessed: 1 | Low: 1 |", text);
    }

    [Fact]
    public void Format_FilelessFindingDetailsAreStandaloneAndAppearOnce()
    {
        var fileless = Finding(null, null).AsPullRequestLevel("Retain the shared invariant.");
        var result = new ReviewResult("Review.", [Finding("src/A.cs", 3), fileless]);

        var text = ReviewSummaryTextFormatter.Format(result, includeFilelessFindingDetails: true);

        Assert.Contains("- **Warning** · `src/A.cs:3`: Check the recorded behavior.", text);
        Assert.Contains("![Severity: Warning]", text);
        Assert.Contains("![Risk: Medium]", text);
        Assert.Contains("\n\nRetain the shared invariant.\n\n<!-- propr-risk-assessment:start -->\n\n| Assessment | Level | Reason |", text);
        Assert.Equal(1, text.Split("Retain the shared invariant.", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("- WARNING:", text);
        Assert.Contains("| 2 | Medium: 2 | High: 2 |", text);
    }

    [Fact]
    public void Format_FileAnchoredFindingWithoutLineKeepsItsLocationInStandaloneDetails()
    {
        var fileless = Finding("src/A.cs", null);

        var text = ReviewSummaryTextFormatter.Format(new ReviewResult("Review.", [fileless]), includeFilelessFindingDetails: true);

        Assert.Contains("**`src/A.cs`**", text);
        Assert.Contains("![Severity: Warning]", text);
        Assert.Contains("![Risk: Medium]", text);
        Assert.Contains("\n\nCheck the recorded behavior.\n\n", text);
        Assert.Contains("| Assessment | Level | Reason |", text);
    }

    [Fact]
    public void Format_ActionTextKeepsMarkdownReadableAndCannotIntroduceExtraActionsOrHtml()
    {
        var finding = new ReviewComment("src/`odd`.cs", 12, CommentSeverity.Warning, "Check `a | b`.\r\n- This is the same finding. <script>bad</script>");

        var text = ReviewSummaryTextFormatter.Format(new ReviewResult("Review.", [finding]));

        Assert.Contains("``src/`odd`.cs:12``", text);
        Assert.Contains("Check `a | b`. - This is the same finding. <\u200Bscript>bad<\u200B/script>", text);
        Assert.Single(text.Split('\n').Where(line => line.StartsWith("- ", StringComparison.Ordinal)));
        Assert.Equal("Check `a | b`.\r\n- This is the same finding. <script>bad</script>", finding.Message);
    }

    [Fact]
    public void Format_CompactActionsUseExcerptsAndKeepAllOfferedFindingTotals()
    {
        var comments = Enumerable.Range(1, 25)
            .Select(index => new ReviewComment($"src/File{index}.cs", index, CommentSeverity.Warning, "Review the recorded request behavior."))
            .ToArray();

        var text = ReviewSummaryTextFormatter.Format(new ReviewResult("Review.", comments));

        Assert.Equal(20, text.Split('\n').Count(line => line.StartsWith("- **Warning**", StringComparison.Ordinal)));
        Assert.Contains("5 additional finding actions omitted from this summary.", text);
        Assert.Contains("full review record", text);
        Assert.Contains("| 25 | Unassessed: 25 | Unassessed: 25 |", text);
        Assert.Equal(25, comments.Length);
    }

    [Fact]
    public void Format_CompactActionBudgetBoundsLongLocationsAndMessages()
    {
        var comments = Enumerable.Range(1, 25)
            .Select(index => new ReviewComment("src/" + new string('x', 2000) + index + ".cs", index, CommentSeverity.Warning, new string('a', 4000)))
            .ToArray();

        var text = ReviewSummaryTextFormatter.Format(new ReviewResult("Review.", comments));
        var actions = text.Split('\n').Where(line => line.StartsWith("- **Warning**", StringComparison.Ordinal)).ToArray();

        Assert.True(actions.Sum(action => action.Length + 2) <= 8000);
        Assert.InRange(actions.Length, 1, 20);
        Assert.Contains($"{25 - actions.Length} additional finding actions omitted from this summary.", text);
        Assert.Contains("| 25 | Unassessed: 25 | Unassessed: 25 |", text);
        Assert.All(comments, comment => Assert.Equal(4000, comment.Message.Length));
    }

    [Fact]
    public void Format_MessageExcerptsDoNotSplitUnicodeScalars()
    {
        var message = new string('x', 319) + "😀" + new string('z', 1000);
        var comment = new ReviewComment("src/Unicode.cs", 7, CommentSeverity.Warning, message);

        var text = ReviewSummaryTextFormatter.Format(new ReviewResult("Review.", [comment]));

        Assert.Contains(new string('x', 319) + "😀…", text);
        Assert.DoesNotContain(new string('z', 1000), text);
        Assert.Equal(text, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text)));
        Assert.Equal(message, comment.Message);
    }

    [Fact]
    public void Format_CompactActionLimitsDoNotRemoveFullFilelessFindingDetails()
    {
        var comments = Enumerable.Range(1, 25)
            .Select(index => new ReviewComment($"src/File{index}.cs", index, CommentSeverity.Warning, "Check the request."))
            .Append(new ReviewComment(null, null, CommentSeverity.Warning, "PR-level details: " + new string('x', 4000)))
            .ToArray();

        var text = ReviewSummaryTextFormatter.Format(new ReviewResult("Review.", comments), includeFilelessFindingDetails: true);

        Assert.Equal(20, text.Split('\n').Count(line => line.StartsWith("- **Warning**", StringComparison.Ordinal)));
        Assert.Contains("5 additional finding actions omitted from this summary.", text);
        Assert.Contains(comments[^1].Message, text);
        Assert.Contains("| 26 | Unassessed: 26 | Unassessed: 26 |", text);
    }

    private static ReviewComment Finding(
        string? path,
        int? line,
        FindingImpact impact = FindingImpact.Moderate,
        FindingLikelihood likelihood = FindingLikelihood.Possible,
        FindingConfidence confidence = FindingConfidence.High) =>
        new(path, line, CommentSeverity.Warning, "Check the recorded behavior.")
        {
            RiskAssessment = new FindingRiskAssessment(
                impact, likelihood, "Recorded consequence.", "Recorded occurrence scenario.",
                "review_model", FindingRiskAssessment.CurrentRubricVersion, confidence, "Recorded supporting evidence."),
        };
}
