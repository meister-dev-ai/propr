// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.Application.Options;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Unit tests for hunk-centered prefetch window behavior in <see cref="FileByFileContextPrefetchStage" />.
///     T101 (a)-(e) + T105 payload field assertions.
/// </summary>
public sealed class FileByFileContextPrefetchStageTests
{
    private static AiReviewOptions OptionsWithBudget(int maxChars = 4000, int before = 40, int after = 15)
    {
        return new AiReviewOptions
        {
            MaxPrefetchRegionChars = maxChars,
            PrefetchWindowLinesBefore = before,
            PrefetchWindowLinesAfter = after,
            MaxPrefetchCallerSites = 0,
        };
    }

    private static string BuildFile(int lineCount, string lineTemplate = "public void Method{0}() {{ var x = {0}; }}")
    {
        return string.Join("\n", Enumerable.Range(1, lineCount).Select(i => string.Format(lineTemplate, i)));
    }

    private static string BuildDiffWithHunkAt(int startLine, int lineCount = 5)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"@@ -{startLine},{lineCount} +{startLine},{lineCount} @@");
        for (var i = 0; i < lineCount; i++)
        {
            sb.AppendLine($"-public void Method{startLine + i}() {{ var x = {startLine + i}; }}");
            sb.AppendLine($"+public void Method{startLine + i}() {{ var x = {startLine + i + 100}; }}");
        }

        return sb.ToString();
    }

    // T101a — large file with mid-file hunk: injected content MUST contain changed-region text,
    // MUST NOT be only the head of the file.
    [Fact]
    public void BuildSurroundingContext_LargeFileWithMidHunk_ContainsChangedRegionNotOnlyHead()
    {
        var file = BuildFile(600);
        // Hunk at lines 390-410 — far from the head.
        var diff = BuildDiffWithHunkAt(390, 10);
        var opts = OptionsWithBudget();

        var (content, truncated, windowedInjection, windowCount, firstWindowStartLine) =
            FileByFileContextPrefetchStage.BuildSurroundingContext(file, diff, opts);

        // Must contain text from around line 390.
        Assert.Contains("Method390", content, StringComparison.Ordinal);
        // Must NOT be only the head of the file (head content is lines 1-30ish).
        // If head-trim, content would start from Method1 and contain Method1 but not Method390.
        // Verify it is windowed injection.
        Assert.True(windowedInjection, "Expected windowed injection for large file with mid-file hunk");
        Assert.True(windowCount > 0, "Expected at least one window");
        Assert.True(firstWindowStartLine.HasValue, "Expected firstWindowStartLine to be set");
        // The window should start well before line 390 (within linesBefore=40 lines).
        Assert.True(firstWindowStartLine!.Value <= 390, "Window should start at or before hunk start line");
    }

    // T101b — two hunks far apart: evidence contains both windows with line-range annotations.
    [Fact]
    public void BuildSurroundingContext_TwoHunksFarApart_ContainsBothWindowsWithAnnotations()
    {
        var file = BuildFile(600);
        var diffSb = new StringBuilder();
        diffSb.AppendLine("@@ -50,3 +50,3 @@");
        diffSb.AppendLine("-public void Method50() { var x = 50; }");
        diffSb.AppendLine("+public void Method50() { var x = 150; }");
        diffSb.AppendLine("@@ -500,3 +500,3 @@");
        diffSb.AppendLine("-public void Method500() { var x = 500; }");
        diffSb.AppendLine("+public void Method500() { var x = 600; }");

        var opts = OptionsWithBudget(8000, 5, 5);
        var (content, _, windowedInjection, windowCount, _) =
            FileByFileContextPrefetchStage.BuildSurroundingContext(file, diffSb.ToString(), opts);

        Assert.True(windowedInjection, "Expected windowed injection");
        // Content must contain text from both regions.
        Assert.Contains("Method50", content, StringComparison.Ordinal);
        Assert.Contains("Method500", content, StringComparison.Ordinal);
        // Content must contain line-range markers.
        Assert.Contains("[lines ", content, StringComparison.Ordinal);
        Assert.True(
            windowCount >= 2 || content.Contains("[lines", StringComparison.Ordinal),
            "Expected two separate windows or merged content with markers");
    }

    // T101c — total evidence respects MaxPrefetchRegionChars and sets Truncated = true when capped.
    [Fact]
    public void BuildSurroundingContext_ContentExceedsBudget_SetsTruncatedTrue()
    {
        // 600 lines of 80-char content each → ~48 000 chars total; budget is 500.
        var file = BuildFile(600, "public void LongMethodNameWithMoreContent{0}() {{ var longVariableName = {0}; }}");
        var diff = BuildDiffWithHunkAt(300);
        var opts = OptionsWithBudget(500);

        var (content, truncated, _, _, _) =
            FileByFileContextPrefetchStage.BuildSurroundingContext(file, diff, opts);

        Assert.True(truncated, "Expected Truncated = true when content is capped");
        Assert.True(content.Length <= 500, $"Content length {content.Length} exceeds budget 500");
    }

    // T101d — file shorter than budget → whole file injected, Truncated = false.
    [Fact]
    public void BuildSurroundingContext_FileWithinBudget_ReturnsWholeFileNotTruncated()
    {
        // 10-line file — fits within default 4000 char budget.
        var file = BuildFile(10);
        var diff = BuildDiffWithHunkAt(5, 2);
        var opts = OptionsWithBudget();

        var (content, truncated, windowedInjection, _, _) =
            FileByFileContextPrefetchStage.BuildSurroundingContext(file, diff, opts);

        Assert.False(truncated, "File within budget should not be truncated");
        Assert.False(windowedInjection, "File within budget should use whole-file injection, not windowed");
        Assert.Contains("Method1", content, StringComparison.Ordinal);
        Assert.Contains("Method10", content, StringComparison.Ordinal);
    }

    // T101e — empty/whitespace UnifiedDiff → falls back to head-of-file trim, does not inject nothing.
    [Fact]
    public void BuildSurroundingContext_EmptyDiff_FallsBackToHeadTrim()
    {
        var file = BuildFile(600);
        var opts = OptionsWithBudget();

        // Empty diff
        var (contentEmpty, truncatedEmpty, windowedEmpty, _, _) =
            FileByFileContextPrefetchStage.BuildSurroundingContext(file, string.Empty, opts);

        Assert.False(string.IsNullOrWhiteSpace(contentEmpty), "Should not inject empty content on empty diff");
        Assert.False(windowedEmpty, "Empty diff should fall back to head-trim, not windowed injection");
        Assert.True(truncatedEmpty, "Head-trim of 600-line file should truncate");

        // Whitespace diff
        var (contentWs, _, windowedWs, _, _) =
            FileByFileContextPrefetchStage.BuildSurroundingContext(file, "   \n  ", opts);

        Assert.False(string.IsNullOrWhiteSpace(contentWs), "Should not inject empty content on whitespace diff");
        Assert.False(windowedWs, "Whitespace diff should fall back to head-trim");
    }

    // T105 — ContextPrefetchApplied protocol payload fields: windowCount, firstWindowStartLine, windowedInjection.
    [Fact]
    public void BuildSurroundingContext_WindowedInjection_ReturnsExpectedPayloadFields()
    {
        var file = BuildFile(600);
        var diff = BuildDiffWithHunkAt(400);
        var opts = OptionsWithBudget(4000, 20, 10);

        var (_, _, windowedInjection, windowCount, firstWindowStartLine) =
            FileByFileContextPrefetchStage.BuildSurroundingContext(file, diff, opts);

        Assert.True(windowedInjection);
        Assert.Equal(1, windowCount);
        Assert.True(firstWindowStartLine.HasValue);
        Assert.True(firstWindowStartLine!.Value > 0, "firstWindowStartLine should be positive");
        // Within linesBefore=20 lines before the hunk start (line 400).
        Assert.True(firstWindowStartLine.Value <= 400, "Window should start at or before hunk");
    }
}
