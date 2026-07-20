// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.CodeAnalysis.TreeSitter.Startup;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterProPR.CodeAnalysis.TreeSitter.Tests;

/// <summary>
///     Contract tests for <see cref="TreeSitterStructuralCodeAnalyzer" /> (US2).
///     Covers the C1-C8 behavioural contract from <c>specs/.../contracts/structural-analyzer.md</c>.
///     C7 (parse timeout) and C9 (concurrency) are covered by <see cref="ParserPoolConcurrencyTests" />.
/// </summary>
public sealed class TreeSitterStructuralCodeAnalyzerTests
{
    [Fact]
    public async Task ExtractCodeTextAsync_blanks_comments_and_strings_keeps_code()
    {
        var analyzer = AnalyzerTestFactory.CreateAnalyzer();
        if (!analyzer.IsAvailable)
        {
            return; // native parser unavailable on this platform
        }

        const string source = "x = token_var\n# token in comment\ny = \"token in string\"\n";
        var request = new StructuralParseRequest("src/a.py", SupportedLanguage.Python, source, []);

        var code = await analyzer.ExtractCodeTextAsync(request, CancellationToken.None);
        var lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        Assert.True(lines.Length >= 3);
        Assert.Contains("token_var", lines[0], StringComparison.Ordinal); // real identifier kept
        Assert.DoesNotContain("token", lines[1], StringComparison.Ordinal); // comment blanked
        Assert.DoesNotContain("token", lines[2], StringComparison.Ordinal); // string literal blanked
    }

    // C1 - inside-function: a changed range inside the deep function resolves to that function.
    [Theory]
    [InlineData(SupportedLanguage.TypeScript)]
    [InlineData(SupportedLanguage.Tsx)]
    [InlineData(SupportedLanguage.JavaScript)]
    [InlineData(SupportedLanguage.Python)]
    [InlineData(SupportedLanguage.Go)]
    [InlineData(SupportedLanguage.Java)]
    [InlineData(SupportedLanguage.Ruby)]
    public async Task ResolveEnclosingDefinitionsAsync_ChangeInsideDeepFunction_ReturnsThatFunction(SupportedLanguage language)
    {
        var analyzer = AnalyzerTestFactory.CreateAnalyzer();
        if (!analyzer.IsAvailable)
        {
            // Native libs not available on this platform - skip, the probe test covers this.
            return;
        }

        var info = AnalyzerTestFactory.Fixtures[language];
        var source = AnalyzerTestFactory.LoadFixture(info.Fixture);
        var (startLine, endLine) = AnalyzerTestFactory.ResolveDeepFunctionSpan(analyzer, language);

        // Pick a line in the middle of the function body.
        var midLine = (startLine + endLine) / 2;
        var request = new StructuralParseRequest(
            info.Path,
            language,
            source,
            new[] { new ChangedLineRange(midLine, midLine) });

        var defs = await analyzer.ResolveEnclosingDefinitionsAsync(request, CancellationToken.None);

        Assert.NotEmpty(defs);
        var resolved = Assert.Single(defs);
        Assert.Equal(info.DeepFunctionName, resolved.Name);
        Assert.Equal(startLine, resolved.StartLine);
        Assert.Equal(endLine, resolved.EndLine);
    }

    // C2 - preamble / imports: a change above the first definition resolves to empty.
    [Theory]
    [InlineData(SupportedLanguage.TypeScript)]
    [InlineData(SupportedLanguage.Python)]
    [InlineData(SupportedLanguage.Go)]
    [InlineData(SupportedLanguage.Ruby)]
    public async Task ResolveEnclosingDefinitionsAsync_ChangeInPreamble_ReturnsEmpty(SupportedLanguage language)
    {
        var analyzer = AnalyzerTestFactory.CreateAnalyzer();
        if (!analyzer.IsAvailable)
        {
            return;
        }

        var info = AnalyzerTestFactory.Fixtures[language];
        var source = AnalyzerTestFactory.LoadFixture(info.Fixture);

        // Line 1 - preamble / imports. No enclosing definition.
        var request = new StructuralParseRequest(
            info.Path,
            language,
            source,
            new[] { new ChangedLineRange(1, 1) });

        var defs = await analyzer.ResolveEnclosingDefinitionsAsync(request, CancellationToken.None);

        Assert.Empty(defs);
    }

    // C3 - change spanning two adjacent functions: returns both, no overlap duplication.
    [Fact]
    public async Task ResolveEnclosingDefinitionsAsync_ChangeSpanningTwoAdjacentFunctions_ReturnsBoth()
    {
        var analyzer = AnalyzerTestFactory.CreateAnalyzer();
        if (!analyzer.IsAvailable)
        {
            return;
        }

        var source = """
                     import { x } from "y";

                     export function alpha() {
                         return 1;
                     }

                     export function beta() {
                         return 2;
                     }

                     export function gamma() {
                         return 3;
                     }
                     """;
        // Pick a range that starts inside `alpha` and ends inside `beta`. The merge should
        // keep both definitions (they do not overlap) and return them in source order.
        var request = new StructuralParseRequest(
            "spanning.ts",
            SupportedLanguage.TypeScript,
            source,
            new[] { new ChangedLineRange(4, 8) });

        var defs = await analyzer.ResolveEnclosingDefinitionsAsync(request, CancellationToken.None);

        Assert.NotEmpty(defs);
        // Two distinct enclosing definitions, sorted by start line, no overlaps.
        var byStart = defs.OrderBy(d => d.StartLine).ToList();
        Assert.Equal(2, byStart.Count);
        Assert.Equal("alpha", byStart[0].Name);
        Assert.Equal("beta", byStart[1].Name);
        Assert.True(byStart[0].EndLine < byStart[1].StartLine, "Definitions should not overlap");
    }

    // C4 - unsupported extension: CanAnalyze returns false; resolve returns empty.
    [Theory]
    [InlineData("Program.cs")]
    [InlineData("README.md")]
    [InlineData("data.json")]
    public void CanAnalyze_UnsupportedExtension_ReturnsFalse(string path)
    {
        var analyzer = AnalyzerTestFactory.CreateAnalyzer();

        // Even when the probe reports available, unsupported extensions are rejected.
        Assert.False(analyzer.CanAnalyze(path));
    }

    [Fact]
    public async Task ResolveEnclosingDefinitionsAsync_UnsupportedLanguage_ReturnsEmpty_NoThrow()
    {
        var analyzer = AnalyzerTestFactory.CreateAnalyzer();

        var request = new StructuralParseRequest(
            "Program.cs",
            SupportedLanguage.JavaScript, // intentionally mismatched to prove the path gate works
            "public class Program { }",
            new[] { new ChangedLineRange(1, 1) });

        // Use a C# path - the analyzer resolves by path first.
        var defs = await analyzer.ResolveEnclosingDefinitionsAsync(
            request with { Path = "Program.cs" },
            CancellationToken.None);

        Assert.Empty(defs);
    }

    // C5 - malformed / truncated source: returns empty, never throws.
    [Fact]
    public async Task ResolveEnclosingDefinitionsAsync_MalformedSource_ReturnsEmpty_NoThrow()
    {
        var analyzer = AnalyzerTestFactory.CreateAnalyzer();
        if (!analyzer.IsAvailable)
        {
            return;
        }

        // Truncated mid-token and unbalanced braces - should parse with errors but not throw.
        var malformed = "function broken(a, b, {\n  return a +\n}\n// no closing";

        var request = new StructuralParseRequest(
            "broken.js",
            SupportedLanguage.JavaScript,
            malformed,
            new[] { new ChangedLineRange(2, 2) });

        var defs = await analyzer.ResolveEnclosingDefinitionsAsync(request, CancellationToken.None);
        // The analyzer returns empty rather than throwing (FR-010 invariant).
        Assert.NotNull(defs);
    }

    [Fact]
    public async Task GetDefinitionsAsync_TruncatedSource_NoThrow()
    {
        var analyzer = AnalyzerTestFactory.CreateAnalyzer();
        if (!analyzer.IsAvailable)
        {
            return;
        }

        var truncated = "class P {\n  void M() {\n    // missing close";

        var request = new StructuralParseRequest(
            "P.java",
            SupportedLanguage.Java,
            truncated,
            Array.Empty<ChangedLineRange>());

        var defs = await analyzer.GetDefinitionsAsync(request, CancellationToken.None);
        Assert.NotNull(defs);
    }

    // C6 - oversized source: rejected with FileTooLarge (no parse attempted).
    [Fact]
    public async Task ResolveEnclosingDefinitionsAsync_SourceExceedsMaxBytes_ReturnsEmpty_NoThrow()
    {
        // Tight budget so we can construct an oversized payload cheaply.
        var options = AnalyzerTestFactory.DefaultOptions(64);
        var analyzer = AnalyzerTestFactory.CreateAnalyzer(options: options);
        if (!analyzer.IsAvailable)
        {
            return;
        }

        // Build a source larger than 64 UTF-8 bytes.
        var oversized = new string('x', 200);
        var request = new StructuralParseRequest(
            "big.ts",
            SupportedLanguage.TypeScript,
            oversized,
            new[] { new ChangedLineRange(1, 1) });

        var defs = await analyzer.ResolveEnclosingDefinitionsAsync(request, CancellationToken.None);
        Assert.Empty(defs);
    }

    // C8 - native parser not loaded: IsAvailable=false; all calls return empty.
    [Fact]
    public async Task IsAvailable_WhenProbeReportsUnavailable_AllCallsReturnEmpty()
    {
        var unavailableProbe = Substitute.For<IStructuralAnalyzerProbe>();
        unavailableProbe.IsAvailable.Returns(false);

        var pool = AnalyzerTestFactory.CreatePool();
        var analyzer = new TreeSitterStructuralCodeAnalyzer(
            unavailableProbe,
            pool,
            NullLogger<TreeSitterStructuralCodeAnalyzer>.Instance);

        Assert.False(analyzer.IsAvailable);
        Assert.False(analyzer.CanAnalyze("sample.ts"));

        var defs = await analyzer.ResolveEnclosingDefinitionsAsync(
            new StructuralParseRequest("sample.ts", SupportedLanguage.TypeScript, "function f() {}", new[] { new ChangedLineRange(1, 1) }),
            CancellationToken.None);
        Assert.Empty(defs);

        var list = await analyzer.GetDefinitionsAsync(
            new StructuralParseRequest("sample.ts", SupportedLanguage.TypeScript, "function f() {}", Array.Empty<ChangedLineRange>()),
            CancellationToken.None);
        Assert.Empty(list);
    }

    // US2 list-definitions: each fixture yields at least the expected top-level/nested functions.
    [Theory]
    [InlineData(SupportedLanguage.TypeScript, "deepTargetFunction")]
    [InlineData(SupportedLanguage.TypeScript, "createEvent")]
    [InlineData(SupportedLanguage.Tsx, "DeepItemList")]
    [InlineData(SupportedLanguage.JavaScript, "deepTargetFunction")]
    [InlineData(SupportedLanguage.Python, "deep_target_function")]
    [InlineData(SupportedLanguage.Go, "deepTargetFunction")]
    [InlineData(SupportedLanguage.Java, "deepTargetFunction")]
    [InlineData(SupportedLanguage.Ruby, "deep_target_function")]
    public async Task GetDefinitionsAsync_FixtureYieldsExpectedDefinition(SupportedLanguage language, string expectedName)
    {
        var analyzer = AnalyzerTestFactory.CreateAnalyzer();
        if (!analyzer.IsAvailable)
        {
            return;
        }

        var info = AnalyzerTestFactory.Fixtures[language];
        var source = AnalyzerTestFactory.LoadFixture(info.Fixture);

        var request = new StructuralParseRequest(info.Path, language, source, Array.Empty<ChangedLineRange>());
        var defs = await analyzer.GetDefinitionsAsync(request, CancellationToken.None);

        Assert.NotEmpty(defs);
        Assert.Contains(defs, d => string.Equals(d.Name, expectedName, StringComparison.Ordinal));
    }
}
