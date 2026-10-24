// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using Markdig;
using Markdig.Syntax;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Tests.Services;

public sealed class ReviewSuggestedFixTests
{
    [Theory]
    [InlineData("old1;\nold2;", false)]
    [InlineData("old1;\r\nold2;", false)]
    [InlineData(" old1;\nold2;", true)]
    public void IdenticalReplacement_IsNotPublishedButMeaningfulWhitespaceIsPreserved(string replacement, bool changesSource)
    {
        var finding = Finding() with { SuggestedFix = Finding().SuggestedFix! with { ReplacementText = replacement } };
        var bound = ReviewSuggestedFixValidator.Bind(finding, [File()], "head");

        Assert.Equal(changesSource, ReviewSuggestedFixRenderer.CanApply(bound, "head"));
        foreach (var native in new[] { false, true })
        {
            var rendered = ReviewSuggestedFixRenderer.RenderMessage(bound, native);
            Assert.Equal(changesSource, rendered.Contains("<!-- propr-suggested-fix:start -->", StringComparison.Ordinal));
            Assert.Contains(finding.Message, rendered);
        }

        Assert.Equal(replacement, bound.SuggestedFix!.ReplacementText);
    }

    [Theory]
    [InlineData("src/Program.CS", "csharp")]
    [InlineData("src/file.ts", "typescript")]
    [InlineData("src/View.TSX", "tsx")]
    [InlineData("src/file.js", "javascript")]
    [InlineData("src/View.jsx", "javascript")]
    [InlineData("src/file.mjs", "javascript")]
    [InlineData("src/file.cjs", "javascript")]
    [InlineData("src/file.py", "python")]
    [InlineData("src/file.pyi", "python")]
    [InlineData("src/file.go", "go")]
    [InlineData("src/File.java", "java")]
    [InlineData("src/file.rb", "ruby")]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("src/file.txt", "")]
    [InlineData("src.cs/README", "")]
    [InlineData("src/Program.cs\nsuggestion", "")]
    public void Render_FallbackLabelsReplacementAndUsesKnownSourceLanguage(string? path, string language)
    {
        var finding = new ReviewComment(path, 2, CommentSeverity.Error, Finding().Message) { SuggestedFix = Finding().SuggestedFix };

        var body = ReviewSuggestedFixRenderer.RenderMessage(finding, false);

        Assert.Equal(
            finding.Message + "\n\n<!-- propr-suggested-fix:start -->\n\n**Suggested change**\n\n```" + language
            + "\n" + finding.SuggestedFix!.ReplacementText + "\n```\n\n<!-- propr-suggested-fix:end -->", body);
    }

    [Fact]
    public void Render_FallbackUsesOriginalSourceLanguageAfterAnchorRemoval()
    {
        var bound = ReviewSuggestedFixValidator.Bind(Finding(), [File()], "head");
        var finding = bound.WithAnchorAndSeverity("other.py", 2, bound.Severity).AsPullRequestLevel(bound.Message);

        var body = ReviewSuggestedFixRenderer.RenderMessage(finding, false);

        Assert.Contains("**Suggested change**\n\n```csharp\n", body);
        Assert.Null(finding.FilePath);
        Assert.Equal("a.cs", finding.SuggestedFix!.SourceFilePath);
    }

    [Theory]
    [InlineData("suggestion")]
    [InlineData("suggestion:-0+1")]
    public void Render_NativeReplacementRetainsFenceInformation(string suggestionInfo)
    {
        var bound = ReviewSuggestedFixValidator.Bind(Finding(), [File()], "head");

        var body = ReviewSuggestedFixRenderer.RenderMessage(bound, true, suggestionInfo);

        Assert.Equal(
            bound.Message + "\n\n<!-- propr-suggested-fix:start -->\n\n```" + suggestionInfo
            + "\n" + bound.SuggestedFix!.ReplacementText + "\n```\n\n<!-- propr-suggested-fix:end -->", body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedOptionalReplacement_DoesNotPreventPublishingTheFinding(bool claimsValidation)
    {
        var finding = Finding();
        var fix = finding.SuggestedFix! with
        {
            ReplacementText = new string('x', 80_000),
            SourceHeadSha = "head",
            SourceFilePath = "a.cs",
            SourceAnchorLineNumber = 2,
            IsSourceValidated = claimsValidation,
        };
        finding = finding with { SuggestedFix = fix };

        Assert.Equal(finding.Message, ReviewSuggestedFixRenderer.RenderMessage(finding, true));
        Assert.False(ReviewSuggestedFixRenderer.CanApply(finding, "head"));
        Assert.Same(fix, finding.SuggestedFix);
    }

    [Theory]
    [InlineData("```suggestion\nvalue<T>();\n```")]
    [InlineData("~~~suggestion:-1+2\r\nvalue<T>();\r\n~~~")]
    [InlineData("> ```suggestion\n> value<T>();\n> ```")]
    [InlineData("- ```suggestion\n  value<T>();\n  ```")]
    [InlineData("```suggestion\nvalue<T>();")]
    [InlineData("    ```suggestion\n    value<T>();\n    ```")]
    public void UnstructuredFences_RemainOrdinaryCodeInMarkdown(string message)
    {
        var finding = new ReviewComment("a.cs", 2, CommentSeverity.Error, message);
        var body = ReviewFindingTextFormatter.Format(finding);
        var html = Markdig.Markdown.ToHtml(body);
        Assert.DoesNotContain("class=\"language-suggestion", html);
        Assert.DoesNotContain("Suggested change", body);
        Assert.DoesNotContain("```csharp", body);
        Assert.Contains("value&lt;T&gt;();", html);
        Assert.Equal(message, finding.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\r\n")]
    [InlineData("    value<T>();\r\n\tNext();\r\n")]
    [InlineData("    value<T>();\n\tNext();")]
    [InlineData("```\n<!-- propr-suggested-fix:start -->\n```\n<!-- propr-suggested-fix:end -->\nvalue<T>();")]
    public void Render_PreservesReplacementBytesAndRestoresRawClaim(string replacement)
    {
        var finding = Finding() with { SuggestedFix = new ReviewSuggestedFix(2, 3, "old1;\nold2;", replacement) };
        var bound = ReviewSuggestedFixValidator.Bind(finding, [File()], "head");
        foreach (var applicable in new[] { false, true })
        {
            var rendered = ReviewSuggestedFixRenderer.RenderMessage(bound, applicable);
            var code = Assert.Single(Markdown.Parse(rendered).OfType<FencedCodeBlock>());
            Assert.Equal(applicable ? "suggestion" : "csharp", code.Info);
            Assert.True(code.ClosingFencedCharCount >= 3);
            var codeStart = rendered.IndexOf('\n', code.Span.Start) + 1;
            Assert.Equal(replacement, rendered[codeStart..(codeStart + replacement.Length)]);
            var fence = new string('`', code.OpeningFencedCharCount);
            Assert.Equal(
                (replacement.EndsWith('\n') ? string.Empty : "\n") + fence + "\n\n<!-- propr-suggested-fix:end -->",
                rendered[(codeStart + replacement.Length)..]);
            foreach (var style in new[] { FindingBadgeStyle.ShieldsIo, FindingBadgeStyle.Plain })
            {
                var body = ReviewFindingTextFormatter.Format(bound, rendered, badgeStyle: style);
                Assert.Equal("ERROR: " + finding.Message, ReviewFindingTextFormatter.StripAssessment(body));
            }
        }

        Assert.Equal(replacement, finding.SuggestedFix.ReplacementText);
    }

    [Theory]
    [InlineData(false, FindingBadgeStyle.ShieldsIo)]
    [InlineData(true, FindingBadgeStyle.ShieldsIo)]
    [InlineData(false, FindingBadgeStyle.Plain)]
    [InlineData(true, FindingBadgeStyle.Plain)]
    public void Truncation_KeepsCompleteReplacementAndApplicability(bool applicable, FindingBadgeStyle style)
    {
        var finding = new ReviewComment("a.cs", 2, CommentSeverity.Error, new string('x', 2000))
        {
            SuggestedFix = Finding().SuggestedFix,
        };
        var bound = ReviewSuggestedFixValidator.Bind(finding, [File()], "head");
        var body = ReviewFindingTextFormatter.Format(bound, ReviewSuggestedFixRenderer.RenderMessage(bound, applicable), badgeStyle: style);
        var shortened = ReviewFindingTextFormatter.Truncate(body, 700);
        Assert.InRange(shortened.Length, 1, 700);
        Assert.Contains(bound.SuggestedFix!.ReplacementText, shortened);
        Assert.Equal(applicable, shortened.Contains("```suggestion", StringComparison.Ordinal));
        Assert.Contains("<!-- propr-suggested-fix:end -->", shortened);
        if (!applicable)
        {
            Assert.Contains("**Suggested change**\n\n```csharp\n", shortened);
        }
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(18)]
    public void Truncation_ReturnsDowngradedFenceWhenNormalizationFitsTheBudget(int maximumLength)
    {
        var shortened = ReviewFindingTextFormatter.Truncate("```suggestion\nx\n```", maximumLength);

        Assert.Equal("```\nx\n```", shortened);
    }

    [Theory]
    [InlineData("suggestion")]
    [InlineData("suggestion:-0+1")]
    public void Truncation_PreservesCompleteNativeFixAtItsExactBudget(string suggestionInfo)
    {
        var finding = new ReviewComment("a.cs", 2, CommentSeverity.Error, new string('x', 2000))
        {
            SuggestedFix = Finding().SuggestedFix,
        };
        var bound = ReviewSuggestedFixValidator.Bind(finding, [File()], "head");
        var rendered = ReviewSuggestedFixRenderer.RenderMessage(bound, true, suggestionInfo);
        var completeFix = rendered[bound.Message.Length..];

        var shortened = ReviewFindingTextFormatter.Truncate(rendered, completeFix.Length);

        Assert.Equal(completeFix, shortened);
        Assert.Equal(suggestionInfo, Assert.Single(Markdown.Parse(shortened).OfType<FencedCodeBlock>()).Info);
    }

    [Theory]
    [InlineData("suggestion")]
    [InlineData("suggestion:-0+1")]
    public void Truncation_PreservesCompleteDowngradedFixWhenNativeFenceExceedsTheBudget(string suggestionInfo)
    {
        var finding = Finding().WithMessage(new string('x', 2000));
        var bound = ReviewSuggestedFixValidator.Bind(finding, [File()], "head");
        var rendered = ReviewSuggestedFixRenderer.RenderMessage(bound, true, suggestionInfo);
        var completePlainFix = "\n\n<!-- propr-suggested-fix:start -->\n\n```\n" + bound.SuggestedFix!.ReplacementText
                                                                                 + "\n```\n\n<!-- propr-suggested-fix:end -->";
        Assert.True(rendered[bound.Message.Length..].Length > completePlainFix.Length);

        var shortened = ReviewFindingTextFormatter.Truncate(rendered, completePlainFix.Length);

        Assert.Equal(completePlainFix, shortened);
        var code = Assert.Single(Markdown.Parse(shortened).OfType<FencedCodeBlock>());
        Assert.True(string.IsNullOrEmpty(code.Info));
        Assert.True(code.ClosingFencedCharCount >= 3);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(80)]
    [InlineData(200)]
    public void Truncation_ShortenedReplacementNeverRemainsApplicable(int maximumLength)
    {
        var finding = Finding() with { SuggestedFix = Finding().SuggestedFix! with { ReplacementText = new string('x', 2000) } };
        var bound = ReviewSuggestedFixValidator.Bind(finding, [File()], "head");
        var body = ReviewFindingTextFormatter.Format(bound, ReviewSuggestedFixRenderer.RenderMessage(bound, true));

        var shortened = ReviewFindingTextFormatter.Truncate(body, maximumLength);

        Assert.InRange(shortened.Length, 0, maximumLength);
        Assert.DoesNotContain("class=\"language-suggestion", Markdown.ToHtml(shortened));
    }

    [Theory]
    [InlineData("Claim.\n\n```csharp\n    value<T>();\n```")]
    [InlineData("Claim.\n\n~~~javascript\n    value<T>();\n~~~")]
    [InlineData("Claim.\n\n```custom-info\n    value<T>();\n```")]
    public void Render_ArbitraryCodeExamplesKeepFenceInformation(string message)
    {
        var finding = new ReviewComment("a.cs", 2, CommentSeverity.Error, message);

        Assert.Equal(message, ReviewSuggestedFixRenderer.RenderMessage(finding, false));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"start_line\":\"2\",\"end_line\":3,\"original_text\":\"old\",\"replacement_text\":\"new\"}")]
    [InlineData("{\"start_line\":2,\"startLine\":3,\"end_line\":3,\"original_text\":\"old\",\"replacement_text\":\"new\"}")]
    [InlineData("{\"start_line\":2,\"end_line\":3,\"original_text\":null,\"replacement_text\":\"new\"}")]
    public void Parse_MalformedOptionalFixIsIgnored(string json)
    {
        using var document = JsonDocument.Parse("{\"suggested_fix\":" + json + "}");
        Assert.Null(ReviewSuggestedFixValidator.Parse(document.RootElement));
    }

    [Fact]
    public void Bind_StaleFixNeverRebindsToANewerRevision()
    {
        var bound = ReviewSuggestedFixValidator.Bind(Finding(), [File()], "old-head");
        var moved = ReviewSuggestedFixValidator.Bind(bound, [File()], "new-head");
        var repeated = ReviewSuggestedFixValidator.Bind(moved, [File()], "new-head");
        Assert.False(ReviewSuggestedFixRenderer.CanApply(repeated, "new-head"));
        Assert.Equal("old-head", repeated.SuggestedFix!.SourceHeadSha);
        Assert.Contains(bound.Message, ReviewSuggestedFixRenderer.RenderMessage(repeated, false));
    }

    [Fact]
    public void Bind_ChangedAnchorWithinTheSameRangeCannotBecomeApplicableAgain()
    {
        var bound = ReviewSuggestedFixValidator.Bind(Finding(), [File()], "head");
        var moved = bound.WithAnchorAndSeverity(bound.FilePath, 3, bound.Severity);
        Assert.False(ReviewSuggestedFixRenderer.CanApply(moved, "head"));
        var rebound = ReviewSuggestedFixValidator.Bind(moved, [File()], "head");
        Assert.False(ReviewSuggestedFixRenderer.CanApply(rebound, "head"));
        Assert.Equal(bound.Message, rebound.Message);
        Assert.Equal(bound.SuggestedFix!.ReplacementText, rebound.SuggestedFix!.ReplacementText);
        var movedBack = moved.WithAnchorAndSeverity(bound.FilePath, bound.LineNumber, bound.Severity);
        Assert.False(ReviewSuggestedFixRenderer.CanApply(ReviewSuggestedFixValidator.Bind(movedBack, [File()], "head"), "head"));
    }

    [Theory]
    [InlineData("@@ -2 +2 @@\n-old;\n+old1;\n")]
    [InlineData("@@ -2,2 +2,2 @@\n-old;\n+old1;\n")]
    [InlineData("@@ -2 +2 @@\n+old1;\n@@ -3 +3 @@\n+old2;\n")]
    public void Bind_RangeOutsideOneVisibleHunkKeepsPlainCode(string diff)
    {
        var bound = ReviewSuggestedFixValidator.Bind(Finding(), [File() with { UnifiedDiff = diff }], "head");
        Assert.False(ReviewSuggestedFixRenderer.CanApply(bound, "head"));
        Assert.Contains(bound.SuggestedFix!.ReplacementText, ReviewSuggestedFixRenderer.RenderMessage(bound, false));
    }

    [Fact]
    public void Bind_PersistedNullSourceCannotRejectFinding()
    {
        var finding = Finding() with { SuggestedFix = new ReviewSuggestedFix(2, 3, null!, null!) };
        var bound = ReviewSuggestedFixValidator.Bind(finding, [File()], "head");
        Assert.False(ReviewSuggestedFixRenderer.CanApply(bound, "head"));
        Assert.Equal(finding.Message, ReviewSuggestedFixRenderer.RenderMessage(bound, false));
    }

    [Fact]
    public void Bind_UsesExactReviewedSourceAndServerRevision()
    {
        var finding = Finding();
        var bound = ReviewSuggestedFixValidator.Bind(finding, [File()], "reviewed-head");
        Assert.Equal("reviewed-head", bound.SuggestedFix!.SourceHeadSha);
        Assert.Equal(finding.Message, bound.Message);
        Assert.Equal(finding.Severity, bound.Severity);
        Assert.True(ReviewSuggestedFixRenderer.CanApply(bound, "reviewed-head"));
        Assert.False(ReviewSuggestedFixRenderer.CanApply(bound, "new-head"));
        Assert.Equal("replacement<T> && \"literal\";", bound.SuggestedFix.ReplacementText);
    }

    [Theory]
    [InlineData("wrong source", 2, 3, "reviewed-head")]
    [InlineData("old1;\nold2;", 1, 2, "reviewed-head")]
    [InlineData("old1;\nold2;", 2, 3, null)]
    public void Bind_InvalidEvidenceKeepsFindingAndPlainReplacement(string original, int start, int end, string? head)
    {
        var finding = Finding() with { SuggestedFix = new ReviewSuggestedFix(start, end, original, "replacement<T> && \"literal\";") };
        var bound = ReviewSuggestedFixValidator.Bind(finding, [File()], head);
        Assert.False(ReviewSuggestedFixRenderer.CanApply(bound, "reviewed-head"));
        var body = ReviewSuggestedFixRenderer.RenderMessage(bound, false);
        Assert.Contains(finding.Message, body);
        Assert.Contains(finding.SuggestedFix.ReplacementText, body);
        Assert.DoesNotContain("```suggestion", body);
        Assert.Contains("**Suggested change**\n\n```csharp\n", body);
        Assert.DoesNotContain("```suggestion", ReviewSuggestedFixRenderer.RenderMessage(bound, true));
    }

    [Fact]
    public void Render_UnstructuredSuggestionCannotBypassValidation()
    {
        var finding = new ReviewComment("a.cs", 2, CommentSeverity.Error, "Claim.\n```suggestion:-1+2\nunsafe<T>();\n```");
        var body = ReviewSuggestedFixRenderer.RenderMessage(finding, true);
        Assert.DoesNotContain("suggestion", body);
        Assert.Contains("unsafe<T>();", body);
        Assert.Equal("Claim.\n```suggestion:-1+2\nunsafe<T>();\n```", finding.Message);
    }

    [Theory]
    [InlineData("<div>\n```suggestion\nunsafe();\n```\n</div>")]
    [InlineData("Claim.\r```suggestion\runsafe();\r```\rFollowing claim.")]
    public void Render_HiddenOrCarriageReturnSuggestionKeepsCodeWithoutApplicability(string message)
    {
        var finding = new ReviewComment("a.cs", 1, CommentSeverity.Error, message);
        var body = ReviewSuggestedFixRenderer.RenderMessage(finding, false);
        Assert.DoesNotContain("suggestion", body);
        Assert.Contains("unsafe();", body);
        if (message.Contains("Following claim.", StringComparison.Ordinal))
        {
            Assert.Contains("Following claim.", body);
        }

        Assert.Equal(message, finding.Message);
    }

    [Fact]
    public void Fix_RoundTripsWithReviewResultAndCannotApplyAfterAnchorChange()
    {
        var bound = ReviewSuggestedFixValidator.Bind(Finding(), [File()], "reviewed-head");
        var restored = JsonSerializer.Deserialize<ReviewResult>(JsonSerializer.Serialize(new ReviewResult("Review.", [bound])))!;
        Assert.Equal(bound.SuggestedFix, Assert.Single(restored.Comments).SuggestedFix);
        Assert.False(ReviewSuggestedFixRenderer.CanApply(bound.WithAnchorAndSeverity("other.cs", 2, bound.Severity), "reviewed-head"));
        Assert.False(ReviewSuggestedFixRenderer.CanApply(bound.AsPullRequestLevel(bound.Message), "reviewed-head"));
    }

    private static ChangedFile File() => new(
        "a.cs", ChangeType.Edit, "before;\nold1;\nold2;\nafter;", "@@ -1,4 +1,4 @@\n before;\n-old;\n+old1;\n+old2;\n after;");

    private static ReviewComment Finding() => new("a.cs", 2, CommentSeverity.Error, "The exact input loses data.")
    {
        SuggestedFix = new ReviewSuggestedFix(2, 3, "old1;\nold2;", "replacement<T> && \"literal\";"),
    };
}
