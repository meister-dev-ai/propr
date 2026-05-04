// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>T047 — Tests for <c>ReviewPrompts.BuildSynthesisUserMessage</c> with enriched findings (US5).</summary>
public class ReviewPromptsEnrichedSynthesisTests
{
    private static readonly IReadOnlyList<(string FilePath, string Summary)> SingleFileSummaries =
        [("src/Foo.cs", "Looks good.")];

    [Fact]
    public void BuildSynthesisUserMessage_WithComments_IncludesFindingsTable()
    {
        var comments = new List<ReviewComment>
        {
            new("src/Foo.cs", 10, CommentSeverity.Error, "Null dereference"),
            new("src/Bar.cs", 20, CommentSeverity.Warning, "Missing using"),
        };

        var result = ReviewPrompts.BuildSynthesisUserMessage(SingleFileSummaries, "My PR", null, comments);

        Assert.Contains("All Per-File Findings", result);
        Assert.Contains("src/Foo.cs", result);
        Assert.Contains("error", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Null dereference", result);
        Assert.Contains("src/Bar.cs", result);
        Assert.Contains("warning", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Missing using", result);
    }

    [Fact]
    public void BuildSynthesisUserMessage_WithComments_IncludesCrossCuttingDirective()
    {
        var comments = new List<ReviewComment>
        {
            new("src/Foo.cs", 5, CommentSeverity.Warning, "Some issue"),
        };

        var result = ReviewPrompts.BuildSynthesisUserMessage(SingleFileSummaries, "My PR", null, comments);

        Assert.Contains("cross_cutting_concerns", result);
    }

    [Fact]
    public void BuildSynthesisUserMessage_WithComments_RequestsStructuredCrossCuttingEvidenceFields()
    {
        var comments = new List<ReviewComment>
        {
            new("src/Foo.cs", 5, CommentSeverity.Warning, "Some issue"),
            new("src/Bar.cs", 8, CommentSeverity.Warning, "Related issue"),
        };

        var result = ReviewPrompts.BuildSynthesisUserMessage(SingleFileSummaries, "My PR", null, comments);

        Assert.Contains("supportingFindingIds", result);
        Assert.Contains("supportingFiles", result);
        Assert.Contains("evidenceResolutionState", result);
        Assert.Contains("candidateSummaryText", result);
    }

    [Fact]
    public void BuildSynthesisUserMessage_WithNoComments_DoesNotIncludeFindingsTableOrDirective()
    {
        var result = ReviewPrompts.BuildSynthesisUserMessage(SingleFileSummaries, "My PR", null, []);

        Assert.DoesNotContain("All Per-File Findings", result);
        Assert.DoesNotContain("cross_cutting_concerns", result);
    }

    [Fact]
    public void BuildSynthesisUserMessage_WithNullComments_DoesNotIncludeFindingsTableOrDirective()
    {
        var result = ReviewPrompts.BuildSynthesisUserMessage(SingleFileSummaries, "My PR", null);

        Assert.DoesNotContain("All Per-File Findings", result);
        Assert.DoesNotContain("cross_cutting_concerns", result);
    }

    [Fact]
    public void BuildSynthesisUserMessage_WithComments_TableHasFileColumnHeader()
    {
        var comments = new List<ReviewComment>
        {
            new("src/ServiceA.cs", null, CommentSeverity.Info, "Note"),
        };

        var result = ReviewPrompts.BuildSynthesisUserMessage(SingleFileSummaries, "PR", null, comments);

        // Markdown table header row should include File, Severity, Message columns
        Assert.Contains("File", result);
        Assert.Contains("Severity", result);
        Assert.Contains("Message", result);
    }
}
