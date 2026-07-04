// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.CodeAnalysis;
using MeisterProPR.CodeAnalysis.Roslyn;
using MeisterProPR.CodeAnalysis.TreeSitter.Parsing;
using MeisterProPR.CodeAnalysis.TreeSitter.Startup;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TreeSitterAnalyzer = MeisterProPR.CodeAnalysis.TreeSitter.TreeSitterStructuralCodeAnalyzer;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Unit tests for hunk-centered prefetch window behavior in <see cref="FileByFileContextPrefetchStage" />.
///     T101 (a)-(e) + T105 payload field assertions + T020 structural boundary integration.
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

    // T020a — deep change in a large TS file with a real analyzer: injected context is the
    // enclosing function (boundary-resolved), not the file head.
    [Fact]
    public async Task BuildSurroundingContextAsync_DeepChangeInLargeTsFile_ReturnsEnclosingFunction()
    {
        var analyzer = CreateRealAnalyzerIfAvailable();
        if (analyzer is null)
        {
            return; // Native libs unavailable on this platform - skip; the analyzer suite covers it.
        }

        // Build a large TS file (>4000 chars) with a known function deep in the file.
        var sb = new StringBuilder();
        sb.AppendLine("import { x } from \"y\";");
        for (var i = 0; i < 80; i++)
        {
            sb.AppendLine($"export function filler{i}() {{ return {i}; }}");
        }

        sb.AppendLine("export function deepTargetFunction(items: number[]): number {");
        sb.AppendLine("    let total = 0;");
        sb.AppendLine("    for (const item of items) {");
        sb.AppendLine("        if (item > 0) { total += item * 2; }");
        sb.AppendLine("    }");
        sb.AppendLine("    return total;");
        sb.AppendLine("}");
        for (var i = 0; i < 80; i++)
        {
            sb.AppendLine($"export function trailing{i}() {{ return {i}; }}");
        }

        var file = sb.ToString();
        // Diff hunk inside deepTargetFunction (line ~86 in the constructed file).
        var deepFnStart = file.Split('\n').ToList().FindIndex(l => l.StartsWith("export function deepTargetFunction", StringComparison.Ordinal)) + 1;
        var diffSb = new StringBuilder();
        diffSb.AppendLine($"@@ -{deepFnStart + 2},3 +{deepFnStart + 2},3 @@");
        diffSb.AppendLine("-        if (item > 0) { total += item * 2; }");
        diffSb.AppendLine("+        if (item > 0) { total += item * 3; }");

        var opts = OptionsWithBudget();

        var result = await FileByFileContextPrefetchStage.BuildSurroundingContextAsync(
            "deep.ts", file, diffSb.ToString(), opts, analyzer, CancellationToken.None);

        Assert.True(result.BoundaryResolved, "Expected boundary-resolved context for a deep change in a supported file");
        Assert.Equal("deepTargetFunction", result.EnclosingSymbol);
        Assert.Null(result.FallbackReason);
        // The rendered content MUST mention the deep function and NOT be only the file head.
        Assert.Contains("deepTargetFunction", result.RenderedContent, StringComparison.Ordinal);
        Assert.DoesNotContain("filler0", result.RenderedContent, StringComparison.Ordinal);
    }

    // T020b — unsupported file (.cs): heuristic fallback unchanged, fallbackReason set.
    [Fact]
    public async Task BuildSurroundingContextAsync_UnsupportedFile_UsesHeuristicFallback()
    {
        var analyzer = CreateRealAnalyzerIfAvailable();
        if (analyzer is null)
        {
            return;
        }

        var file = BuildFile(600);
        var diff = BuildDiffWithHunkAt(400);
        var opts = OptionsWithBudget();

        var result = await FileByFileContextPrefetchStage.BuildSurroundingContextAsync("Program.cs", file, diff, opts, analyzer, CancellationToken.None);

        Assert.False(result.BoundaryResolved);
        Assert.Null(result.EnclosingSymbol);
        Assert.Equal(FallbackReason.UnsupportedLanguage, result.FallbackReason);
        // Heuristic still produces windowed content.
        Assert.True(result.WindowCount > 0);
    }

    // T020c — oversized file: heuristic fallback with FileTooLarge reason (no parse attempted).
    [Fact]
    public async Task BuildSurroundingContextAsync_OversizedFile_UsesHeuristicFallbackWithFileTooLarge()
    {
        var analyzer = CreateRealAnalyzerIfAvailable();
        if (analyzer is null)
        {
            return;
        }

        // Construct a TS file larger than the max parse bytes (512 KB default -> use a tight override).
        // The source must also exceed MaxPrefetchRegionChars so the prefetch stage doesn't take
        // the whole-file injection path before the analyzer is consulted.
        var opts = new AiReviewOptions
        {
            MaxPrefetchRegionChars = 4000,
            PrefetchWindowLinesBefore = 40,
            PrefetchWindowLinesAfter = 15,
            MaxPrefetchCallerSites = 0,
            MaxStructuralParseBytes = 64, // tiny so the oversized path triggers
        };

        var big = new string('x', 5000) + "\n"; // >4000 chars (skips whole-file) and >64 bytes (triggers FileTooLarge)
        var diff = BuildDiffWithHunkAt(1, 1);

        var result = await FileByFileContextPrefetchStage.BuildSurroundingContextAsync("big.ts", big, diff, opts, analyzer, CancellationToken.None);

        Assert.False(result.BoundaryResolved);
        Assert.Equal(FallbackReason.FileTooLarge, result.FallbackReason);
    }

    // T020d — kill-switch off: heuristic everywhere, AnalyzerDisabled reason.
    [Fact]
    public async Task BuildSurroundingContextAsync_KillSwitchOff_UsesHeuristicWithAnalyzerDisabled()
    {
        var analyzer = CreateRealAnalyzerIfAvailable();
        if (analyzer is null)
        {
            return;
        }

        var file = BuildFile(600);
        var diff = BuildDiffWithHunkAt(400);
        var opts = OptionsWithBudget();
        opts.EnableStructuralBoundaryResolution = false;

        var result = await FileByFileContextPrefetchStage.BuildSurroundingContextAsync("sample.ts", file, diff, opts, analyzer, CancellationToken.None);

        Assert.False(result.BoundaryResolved);
        Assert.Equal(FallbackReason.AnalyzerDisabled, result.FallbackReason);
    }

    // T020e — analyzer unavailable (probe reports false): heuristic with NativeUnavailable reason.
    [Fact]
    public async Task BuildSurroundingContextAsync_AnalyzerUnavailable_UsesHeuristicWithNativeUnavailable()
    {
        var unavailable = Substitute.For<IStructuralCodeAnalyzer>();
        unavailable.IsAvailable.Returns(false);
        unavailable.CanAnalyze(Arg.Any<string>()).Returns(false);

        var file = BuildFile(600);
        var diff = BuildDiffWithHunkAt(400);
        var opts = OptionsWithBudget();

        var result = await FileByFileContextPrefetchStage.BuildSurroundingContextAsync("sample.ts", file, diff, opts, unavailable, CancellationToken.None);

        Assert.False(result.BoundaryResolved);
        Assert.Equal(FallbackReason.NativeUnavailable, result.FallbackReason);
    }

    // T020f — change in preamble (no enclosing definition): heuristic with NoEnclosingDefinition.
    [Fact]
    public async Task BuildSurroundingContextAsync_ChangeInPreamble_FallsBackWithNoEnclosingDefinition()
    {
        var analyzer = CreateRealAnalyzerIfAvailable();
        if (analyzer is null)
        {
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("import { x } from \"y\";");
        sb.AppendLine("const PRELUDE = 1;");
        sb.AppendLine(); // blank line keeps the diff-extracted range (2,3) inside the preamble.
        // Pad with enough filler that the file exceeds the 4000-char budget so the structural
        // path is actually exercised (small files return whole-file injection).
        for (var i = 0; i < 120; i++)
        {
            sb.AppendLine($"export function filler{i}() {{ return {i}; }}");
        }

        var file = sb.ToString();
        var diffSb = new StringBuilder();
        diffSb.AppendLine("@@ -2,1 +2,1 @@");
        diffSb.AppendLine("-const PRELUDE = 1;");
        diffSb.AppendLine("+const PRELUDE = 2;");

        var opts = OptionsWithBudget();

        var result = await FileByFileContextPrefetchStage.BuildSurroundingContextAsync(
            "preamble.ts", file, diffSb.ToString(), opts, analyzer, CancellationToken.None);

        Assert.False(result.BoundaryResolved);
        Assert.Equal(FallbackReason.NoEnclosingDefinition, result.FallbackReason);
    }

    // Parity — the seven Tree-sitter languages route through the composite with byte-identical
    // output to the bare Tree-sitter analyzer, and C# now resolves structurally via the
    // Roslyn-syntax backend behind the same composite.
    [Fact]
    public async Task Parity_NonCSharp_CompositeMatchesBareTreeSitter()
    {
        var treeSitter = CreateRealAnalyzerIfAvailable();
        if (treeSitter is null)
        {
            return; // Native libs unavailable - the analyzer suite covers parity directly.
        }

        var composite = new CompositeStructuralCodeAnalyzer([treeSitter, CreateRoslynAnalyzer()]);

        var sb = new StringBuilder();
        sb.AppendLine("import { x } from \"y\";");
        for (var i = 0; i < 80; i++)
        {
            sb.AppendLine($"export function filler{i}() {{ return {i}; }}");
        }

        sb.AppendLine("export function deepTargetFunction(items: number[]): number {");
        sb.AppendLine("    let total = 0;");
        sb.AppendLine("    for (const item of items) { total += item; }");
        sb.AppendLine("    return total;");
        sb.AppendLine("}");
        var file = sb.ToString();
        var deepFnStart = file.Split('\n').ToList().FindIndex(l => l.StartsWith("export function deepTargetFunction", StringComparison.Ordinal)) + 1;
        var diffSb = new StringBuilder();
        diffSb.AppendLine($"@@ -{deepFnStart + 2},1 +{deepFnStart + 2},1 @@");
        diffSb.AppendLine("-    for (const item of items) { total += item; }");
        diffSb.AppendLine("+    for (const item of items) { total += item * 2; }");
        var diff = diffSb.ToString();
        var opts = OptionsWithBudget();

        var viaBare = await FileByFileContextPrefetchStage.BuildSurroundingContextAsync("deep.ts", file, diff, opts, treeSitter, CancellationToken.None);
        var viaComposite = await FileByFileContextPrefetchStage.BuildSurroundingContextAsync("deep.ts", file, diff, opts, composite, CancellationToken.None);

        Assert.Equal(viaBare.BoundaryResolved, viaComposite.BoundaryResolved);
        Assert.Equal(viaBare.EnclosingSymbol, viaComposite.EnclosingSymbol);
        Assert.Equal(viaBare.RenderedContent, viaComposite.RenderedContent);
        Assert.Equal(viaBare.FallbackReason, viaComposite.FallbackReason);
    }

    [Fact]
    public async Task CSharp_DeepChange_ResolvesStructurallyViaRoslyn()
    {
        var treeSitter = CreateRealAnalyzerIfAvailable();
        // C# only needs the (always-available) Roslyn backend; Tree-sitter is optional here.
        var backends = treeSitter is null
            ? new IStructuralCodeAnalyzer[] { CreateRoslynAnalyzer() }
            : [treeSitter, CreateRoslynAnalyzer()];
        var composite = new CompositeStructuralCodeAnalyzer(backends);

        var sb = new StringBuilder();
        sb.AppendLine("namespace Sample;");
        sb.AppendLine("public sealed class Calculator");
        sb.AppendLine("{");
        for (var i = 0; i < 80; i++)
        {
            sb.AppendLine($"    public int Filler{i}() {{ return {i}; }}");
        }

        sb.AppendLine("    public int DeepTargetMethod(int[] items)");
        sb.AppendLine("    {");
        sb.AppendLine("        var total = 0;");
        sb.AppendLine("        foreach (var item in items) { total += item; }");
        sb.AppendLine("        return total;");
        sb.AppendLine("    }");
        for (var i = 0; i < 80; i++)
        {
            sb.AppendLine($"    public int Trailing{i}() {{ return {i}; }}");
        }

        sb.AppendLine("}");
        var file = sb.ToString();
        var deepStart = file.Split('\n').ToList().FindIndex(l => l.Contains("DeepTargetMethod", StringComparison.Ordinal)) + 1;
        var diffSb = new StringBuilder();
        diffSb.AppendLine($"@@ -{deepStart + 3},1 +{deepStart + 3},1 @@");
        diffSb.AppendLine("-        foreach (var item in items) { total += item; }");
        diffSb.AppendLine("+        foreach (var item in items) { total += item * 2; }");
        var opts = OptionsWithBudget();

        var result = await FileByFileContextPrefetchStage.BuildSurroundingContextAsync(
            "Calculator.cs", file, diffSb.ToString(), opts, composite, CancellationToken.None);

        Assert.True(result.BoundaryResolved, "C# should now resolve structurally through the Roslyn backend.");
        Assert.Equal("DeepTargetMethod", result.EnclosingSymbol);
        Assert.Null(result.FallbackReason);
        Assert.Contains("DeepTargetMethod", result.RenderedContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Filler0(", result.RenderedContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CSharp_KillSwitchOff_FallsBackToHeuristic_NeverWorse()
    {
        var composite = new CompositeStructuralCodeAnalyzer([CreateRoslynAnalyzer()]);
        var file = BuildFile(600);
        var diff = BuildDiffWithHunkAt(400);
        var opts = OptionsWithBudget();
        opts.EnableStructuralBoundaryResolution = false;

        var result = await FileByFileContextPrefetchStage.BuildSurroundingContextAsync("Program.cs", file, diff, opts, composite, CancellationToken.None);

        // Kill-switch off → heuristic everywhere (incl. C#); still produces windowed content (never worse).
        Assert.False(result.BoundaryResolved);
        Assert.Equal(FallbackReason.AnalyzerDisabled, result.FallbackReason);
        Assert.True(result.WindowCount > 0);
    }

    internal static RoslynSyntaxStructuralAnalyzer CreateRoslynAnalyzer()
    {
        var options = Microsoft.Extensions.Options.Options.Create(
            new AiReviewOptions
            {
                MaxStructuralParseBytes = 524_288,
                StructuralParseTimeoutMs = 1_000,
            });
        return new RoslynSyntaxStructuralAnalyzer(options, NullLogger<RoslynSyntaxStructuralAnalyzer>.Instance);
    }

    internal static IStructuralCodeAnalyzer? CreateRealAnalyzerIfAvailable()
    {
        // Construct the real analyzer from the CodeAnalysis.TreeSitter project via direct
        // reference (InternalsVisibleTo grants access). If the native libs are not available
        // on this platform, return null so callers skip the test.
        var probe = new TreeSitterNativeProbe(NullLogger<TreeSitterNativeProbe>.Instance);
        if (!probe.IsAvailable)
        {
            return null;
        }

        var options = Microsoft.Extensions.Options.Options.Create(
            new AiReviewOptions
            {
                MaxFileReviewConcurrency = 3,
                MaxStructuralParseBytes = 524_288,
                StructuralParseTimeoutMs = 1_000,
            });

        var pool = new ParserPool(3, 524_288, 1_000);
        return new TreeSitterAnalyzer(
            probe,
            pool,
            options,
            NullLogger<TreeSitterAnalyzer>.Instance);
    }
}

/// <summary>
///     T024 — end-to-end trace event verification through the stage's ExecuteAsync.
///     Verifies SC-001 (boundary-resolved context for a deep change) and SC-005
///     (the trace records boundaryResolver="tree-sitter" + enclosingSymbol).
/// </summary>
public sealed class FileByFileContextPrefetchStageTraceTests
{
    [Fact]
    public async Task ExecuteAsync_DeepChangeInTsFile_TraceRecordsTreeSitterBoundaryResolver()
    {
        var analyzer = FileByFileContextPrefetchStageTests.CreateRealAnalyzerIfAvailable();
        if (analyzer is null)
        {
            return; // Native libs unavailable on this platform.
        }

        // Build a large TS file with a deep function (exceeds 4000-char budget so the
        // structural path is exercised rather than whole-file injection).
        var sb = new StringBuilder();
        sb.AppendLine("import { x } from \"y\";");
        for (var i = 0; i < 80; i++)
        {
            sb.AppendLine($"export function filler{i}() {{ return {i}; }}");
        }

        sb.AppendLine("export function deepTargetFunction(items: number[]): number {");
        sb.AppendLine("    let total = 0;");
        sb.AppendLine("    for (const item of items) {");
        sb.AppendLine("        if (item > 0) { total += item * 2; }");
        sb.AppendLine("    }");
        sb.AppendLine("    return total;");
        sb.AppendLine("}");
        for (var i = 0; i < 80; i++)
        {
            sb.AppendLine($"export function trailing{i}() {{ return {i}; }}");
        }

        var file = sb.ToString();
        var lines = file.Split('\n');
        var deepFnStart = Array.FindIndex(lines, l => l.StartsWith("export function deepTargetFunction", StringComparison.Ordinal)) + 1;
        var diffSb = new StringBuilder();
        diffSb.AppendLine($"@@ -{deepFnStart + 2},3 +{deepFnStart + 2},3 @@");
        diffSb.AppendLine("-        if (item > 0) { total += item * 2; }");
        diffSb.AppendLine("+        if (item > 0) { total += item * 3; }");

        var opts = new AiReviewOptions
        {
            MaxPrefetchRegionChars = 4000,
            PrefetchWindowLinesBefore = 40,
            PrefetchWindowLinesAfter = 15,
            MaxPrefetchCallerSites = 0, // skip caller-site search
        };

        // Capture the recorded event details via NSubstitute.
        string? capturedDetails = null;
        var recorder = Substitute.For<IProtocolRecorder>();
        recorder.RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Do<string?>(s => capturedDetails = s),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var stage = new FileByFileContextPrefetchStage(opts, recorder, analyzer);

        var changedFile = new ChangedFile("deep.ts", ChangeType.Edit, file, diffSb.ToString());
        var fileReviewContext = new ReviewSystemContext(null, [], Substitute.For<IReviewContextTools>())
        {
            PerFileHint = new PerFileReviewHint("deep.ts", 1, 1, Array.Empty<ChangedFileSummary>()),
        };
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://org", "proj", "repo", 1, 1);
        var context = new PerFileReviewContext(
            job,
            changedFile,
            null,
            fileReviewContext,
            Guid.NewGuid(),
            null,
            null);

        await stage.ExecuteAsync(context, CancellationToken.None);

        // SC-005: the trace event must record the boundary resolver and enclosing symbol.
        Assert.NotNull(capturedDetails);
        Assert.Contains("\"boundaryResolver\":\"tree-sitter\"", capturedDetails, StringComparison.Ordinal);
        Assert.Contains("\"enclosingSymbol\":\"deepTargetFunction\"", capturedDetails, StringComparison.Ordinal);
        Assert.Contains("\"fallbackReason\":null", capturedDetails, StringComparison.Ordinal);
    }
}

/// <summary>
///     The deterministic caller-evidence feed. A changed C# definition with a
///     cross-file caller injects a <c>supported_caller_site</c> evidence item; a change with no
///     external callers injects none (no regression).
/// </summary>
public sealed class FileByFileContextPrefetchStageUs4Tests
{
    private const string CalcSource =
        "namespace N;\n" +
        "public class Calc\n" +
        "{\n" +
        "    public int CalcTotal(int[] items)\n" +
        "    {\n" +
        "        var total = 0;\n" +
        "        foreach (var i in items) { total += i; }\n" +
        "        return total;\n" +
        "    }\n" +
        "}\n";

    private const string CalcDiff =
        "@@ -7,1 +7,1 @@\n" +
        "-        foreach (var i in items) { total += i; }\n" +
        "+        foreach (var i in items) { total += i * 2; }\n";

    private const string UserSource =
        "namespace N;\n" +
        "public class User\n" +
        "{\n" +
        "    public int Run(Calc c) => c.CalcTotal(new[] { 1 });\n" +
        "}\n";

    private static (FileByFileContextPrefetchStage Stage, PerFileReviewContext Context) BuildScenario(IReadOnlyDictionary<string, string> workspaceFiles)
    {
        var reviewTools = StructuralReferenceToolTestHarness.CreateTools(workspaceFiles);
        var opts = new AiReviewOptions
        {
            MaxPrefetchRegionChars = 4000,
            MaxPrefetchCallerSites = 5,
            EnableStructuralReferenceTools = true,
            MaxReferenceCandidateFiles = 200,
            MaxReferenceResults = 50,
            MaxReferenceResultChars = 8000,
            ReferenceResolutionTimeoutMs = 4000,
        };

        var stage = new FileByFileContextPrefetchStage(opts, null, FileByFileContextPrefetchStageTests.CreateRoslynAnalyzer());

        var changedFile = new ChangedFile("Calc.cs", ChangeType.Edit, CalcSource, CalcDiff);
        var fileReviewContext = new ReviewSystemContext(null, [], reviewTools)
        {
            PerFileHint = new PerFileReviewHint("Calc.cs", 1, 1, Array.Empty<ChangedFileSummary>()),
        };
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://org", "proj", "repo", 1, 1);
        var context = new PerFileReviewContext(job, changedFile, null, fileReviewContext, null, null, null);
        return (stage, context);
    }

    [Fact]
    public async Task ChangedDefinitionWithCrossFileCaller_InjectsSupportedCallerSite()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Calc.cs"] = CalcSource,
            ["User.cs"] = UserSource,
        };

        var (stage, context) = BuildScenario(files);
        await stage.ExecuteAsync(context, CancellationToken.None);

        var evidence = context.FileReviewContext.PerFileHint!.PrefetchedContextEvidence;
        Assert.Contains(
            evidence,
            e => string.Equals(e.Kind, "supported_caller_site", StringComparison.Ordinal)
                 && e.SourceId.Contains("User.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangedDefinitionWithCrossFileCaller_CallerSiteCarriesSnippetAndEnclosingSymbol()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Calc.cs"] = CalcSource,
            ["User.cs"] = UserSource,
        };

        var (stage, context) = BuildScenario(files);
        await stage.ExecuteAsync(context, CancellationToken.None);

        var callerSite = Assert.Single(
            context.FileReviewContext.PerFileHint!.PrefetchedContextEvidence,
            e => string.Equals(e.Kind, "supported_caller_site", StringComparison.Ordinal)
                 && e.SourceId.Contains("User.cs", StringComparison.Ordinal));

        // The feed item carries the enclosing symbol and the matched line snippet as structured fields...
        Assert.Equal("Run", callerSite.EnclosingSymbol);
        Assert.False(string.IsNullOrWhiteSpace(callerSite.MatchedSnippet));
        Assert.Contains("CalcTotal", callerSite.MatchedSnippet!, StringComparison.Ordinal);
        Assert.True(callerSite.MatchedSnippet!.Length <= ReferenceSnippetEnricher.MaxSnippetChars);

        // ...and both are embedded in the rendered content the reviewer sees.
        Assert.Contains("in `Run`", callerSite.Content, StringComparison.Ordinal);
        Assert.Contains(callerSite.MatchedSnippet, callerSite.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChangedDefinitionWithNoExternalCallers_InjectsNoCallerSite()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Calc.cs"] = CalcSource, // no caller file in the workspace
        };

        var (stage, context) = BuildScenario(files);
        await stage.ExecuteAsync(context, CancellationToken.None);

        var evidence = context.FileReviewContext.PerFileHint!.PrefetchedContextEvidence ?? [];
        Assert.DoesNotContain(evidence, e => string.Equals(e.Kind, "supported_caller_site", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangedDefinitionWithCrossFileCaller_RecordsMeasuredFanOut()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Calc.cs"] = CalcSource,
            ["User.cs"] = UserSource,
        };

        var (stage, context) = BuildScenario(files);
        await stage.ExecuteAsync(context, CancellationToken.None);

        // The deterministic fan-out signal is extracted from the same reference resolution.
        var fanOut = context.FileReviewContext.PerFileHint!.FanOut;
        Assert.Equal(FanOutKind.Measured, fanOut.Kind);
        Assert.True(fanOut.HasData);
        Assert.True(fanOut.Count >= 1); // CalcTotal is referenced by User.cs
    }

    [Fact]
    public async Task NonAnalyzableChangedFile_RecordsUnavailableFanOut()
    {
        var reviewTools = StructuralReferenceToolTestHarness.CreateTools(new Dictionary<string, string>(StringComparer.Ordinal));
        var opts = new AiReviewOptions
        {
            MaxPrefetchCallerSites = 5,
            EnableStructuralReferenceTools = true,
            MaxReferenceCandidateFiles = 200,
            MaxReferenceResults = 50,
            MaxReferenceResultChars = 8000,
            ReferenceResolutionTimeoutMs = 4000,
        };
        var stage = new FileByFileContextPrefetchStage(opts, null, FileByFileContextPrefetchStageTests.CreateRoslynAnalyzer());

        var changedFile = new ChangedFile("notes.md", ChangeType.Edit, "# token heading\n", "@@ -0,0 +1,1 @@\n+# token heading\n");
        var fileReviewContext = new ReviewSystemContext(null, [], reviewTools)
        {
            PerFileHint = new PerFileReviewHint("notes.md", 1, 1, Array.Empty<ChangedFileSummary>()),
        };
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://org", "proj", "repo", 1, 1);
        var context = new PerFileReviewContext(job, changedFile, null, fileReviewContext, null, null, null);

        await stage.ExecuteAsync(context, CancellationToken.None);

        // absence != zero: a non-parseable file yields no fan-out signal, never a measured zero.
        var fanOut = context.FileReviewContext.PerFileHint!.FanOut;
        Assert.Equal(FanOutKind.Unavailable, fanOut.Kind);
        Assert.False(fanOut.HasData);
    }
}
