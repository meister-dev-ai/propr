// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Tests.Services;

public sealed class ReviewPresentationTemplateTests
{
    [Fact]
    public void SummaryCompositionKeepsSeparatorsAroundBoundedActionsAndOmissionNotice()
    {
        var comments = Enumerable.Range(1, 25).Select(index => new ReviewComment($"File{index}.cs", index, CommentSeverity.Warning, "Item.")).ToArray();
        var result = new ReviewResult("Conclusion.", comments);

        var rendered = ReviewSummaryTextFormatter.Format(result);

        Assert.Equal(
            "**Findings to review**\n\n"
            + string.Join("\n\n", Enumerable.Range(1, 20).Select(index => $"- **Warning** · `File{index}.cs:{index}`: Item."))
            + "\n\n5 additional finding actions omitted from this summary. See the full review record or individual finding comments when available."
            + "\n\n| Findings to report | Risk levels | Confidence levels |\n| --- | --- | --- |\n| 25 | Unassessed: 25 | Unassessed: 25 |", rendered);
    }

    [Fact]
    public void SummaryCompositionRetainsActionSpacingAndCompleteProviderRenderedDetails()
    {
        const string details = "Provider `{{literal}}` details.\r\n```csharp\r\nreturn \"<tag>\";\r\n```\r\n";
        var result = new ReviewResult(
            "Conclusion.",
            [new("a.cs", 1, CommentSeverity.Error, "First."), new("b.cs", null, CommentSeverity.Warning, "Second.")]);

        var rendered = ReviewSummaryTextFormatter.Format(result, includeFilelessFindingDetails: true, findingRenderer: _ => details);

        Assert.Equal(
            "**Findings to review**\n\n- **Error** · `a.cs:1`: First.\n\n**`b.cs`**\n\n" + details
                                                                                         + "\n\n| Findings to report | Risk levels | Confidence levels |\n| --- | --- | --- |\n| 2 | Unassessed: 2 | Unassessed: 2 |",
            rendered);
    }

    [Fact]
    public void HtmlAssessmentDoesNotRenderUnusedMarkdownCells()
    {
        var finding = new ReviewComment("a.cs", 3, CommentSeverity.Error, "The supplied input loses data.")
        {
            RiskAssessment = new(
                FindingImpact.High, FindingLikelihood.Likely, "Data is lost.", "Every call.", "review_model", FindingRiskAssessment.CurrentRubricVersion),
        };

        var rendered = ReviewFindingTextFormatter.Format(
            finding, renderedMessage: finding.Message, htmlAssessment: true,
            renderText: _ => throw new InvalidOperationException("Markdown assessment cells must not be rendered."));

        Assert.Contains("<details><summary>Assessment details</summary><table>", rendered);
        Assert.Contains("<tr><td>Impact</td><td>High</td><td>Data is lost.</td></tr>", rendered);
        Assert.Equal("ERROR: " + finding.Message, ReviewFindingTextFormatter.StripAssessment(rendered));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FindingCompositionPreservesLiteralTemplateTokensAndReplacementBytes(bool htmlAssessment)
    {
        const string source = "return oldValue;";
        const string replacement = "return \"{{#if value}} <tag> | ` 😀 {{/if}}\";\r\n";
        var finding = new ReviewComment("a.cs", 3, CommentSeverity.Error, "Keep `{{name}}` unchanged.\r\n```csharp\r\nvar x = \"<tag>\";\r\n```")
        {
            SuggestedFix = new(3, 3, source, replacement),
            RiskAssessment = new(
                FindingImpact.High, FindingLikelihood.Likely, "The `{{value}}` input loses data.", "Every call.", "review_model",
                FindingRiskAssessment.CurrentRubricVersion),
        };

        var rendered = ReviewFindingTextFormatter.Format(finding, collapseAssessment: true, htmlAssessment: htmlAssessment);

        Assert.Contains("Keep `{{name}}` unchanged.\r\n```csharp\r\nvar x = \"<tag>\";\r\n```", rendered);
        Assert.Contains(replacement, rendered);
        Assert.Contains("{{value}}", rendered);
        Assert.Equal("ERROR: " + finding.Message, ReviewFindingTextFormatter.StripAssessment(rendered));
    }
}
