// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MeisterProPR.CodeAnalysis.Roslyn.Tests;

public sealed class RoslynSyntaxStructuralAnalyzerTests
{
    private static readonly string FixtureSource =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "OrderService.cs"));

    private static RoslynSyntaxStructuralAnalyzer Analyzer(AiReviewOptions? options = null)
    {
        return new RoslynSyntaxStructuralAnalyzer(Options.Create(options ?? new AiReviewOptions()), NullLogger<RoslynSyntaxStructuralAnalyzer>.Instance);
    }

    private static StructuralParseRequest Request(string source, params (int Start, int End)[] ranges)
    {
        return new StructuralParseRequest(
            "src/OrderService.cs",
            SupportedLanguage.CSharp,
            source,
            ranges.Select(r => new ChangedLineRange(r.Start, r.End)).ToList());
    }

    private static int LineContaining(string substring)
    {
        var lines = FixtureSource.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(substring, StringComparison.Ordinal))
            {
                return i + 1; // 1-based
            }
        }

        throw new InvalidOperationException($"Fixture line not found: {substring}");
    }

    [Fact]
    public async Task ExtractCodeTextAsync_blanks_comments_and_strings_keeps_code()
    {
        const string source = "var token = GetValue();\n// token in a comment\nvar s = \"token in a string\";\n";

        var code = await Analyzer().ExtractCodeTextAsync(Request(source), CancellationToken.None);
        var lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        Assert.True(lines.Length >= 3);
        Assert.Contains("token", lines[0], StringComparison.Ordinal); // real identifier kept
        Assert.DoesNotContain("token", lines[1], StringComparison.Ordinal); // comment blanked
        Assert.DoesNotContain("token", lines[2], StringComparison.Ordinal); // string literal blanked
    }

    [Fact]
    public async Task ExtractCodeTextAsync_empty_source_returns_empty()
    {
        Assert.Equal(string.Empty, await Analyzer().ExtractCodeTextAsync(Request(string.Empty), CancellationToken.None));
    }

    [Fact]
    public void CanAnalyze_only_cs_files()
    {
        var analyzer = Analyzer();
        Assert.True(analyzer.IsAvailable);
        Assert.True(analyzer.CanAnalyze("src/A.cs"));
        Assert.True(analyzer.CanAnalyze("src/A.CS"));
        Assert.False(analyzer.CanAnalyze("src/a.ts"));
        Assert.False(analyzer.CanAnalyze(string.Empty));
    }

    [Fact]
    public async Task GetDefinitions_lists_types_and_members()
    {
        var defs = await Analyzer().GetDefinitionsAsync(Request(FixtureSource), CancellationToken.None);

        Assert.Contains(defs, d => d is { Kind: DefinitionKind.Class, Name: "OrderService" });
        Assert.Contains(defs, d => d is { Kind: DefinitionKind.Class, Name: "PricingPolicy" });
        Assert.Contains(defs, d => d is { Kind: DefinitionKind.Method, Name: "CalculateTotal" });
        Assert.Contains(defs, d => d is { Kind: DefinitionKind.Method, Name: "Describe" });
    }

    [Fact]
    public async Task ResolveEnclosingDefinitions_returns_innermost_member()
    {
        var describeLine = LineContaining("public string Describe(");
        var enclosing = await Analyzer().ResolveEnclosingDefinitionsAsync(
            Request(FixtureSource, (describeLine + 1, describeLine + 1)),
            CancellationToken.None);

        Assert.Contains(enclosing, e => e is { Kind: DefinitionKind.Method, Name: "Describe" });
        // Innermost wins: the containing class is not also returned for a change inside the method.
        Assert.DoesNotContain(enclosing, e => e.Name == "OrderService");
    }

    [Fact]
    public async Task ConfirmReferenceLines_includes_real_uses_and_excludes_comment_and_string()
    {
        var callLine = LineContaining("this.CalculateTotal(quantity");
        var commentLine = LineContaining("// This mention of CalculateTotal");
        var stringLine = LineContaining("CalculateTotal was not actually invoked");

        var lines = await Analyzer().ConfirmReferenceLinesAsync(Request(FixtureSource), "CalculateTotal", CancellationToken.None);

        Assert.Contains(callLine, lines); // real call confirmed
        Assert.DoesNotContain(commentLine, lines); // comment excluded
        Assert.DoesNotContain(stringLine, lines); // string excluded
    }

    [Fact]
    public async Task ConfirmReferenceLines_returns_distinct_ascending_lines()
    {
        var lines = await Analyzer().ConfirmReferenceLinesAsync(Request(FixtureSource), "CalculateTotal", CancellationToken.None);

        Assert.Equal(lines.OrderBy(static l => l).Distinct(), lines); // distinct, ascending
    }

    [Fact]
    public async Task Malformed_source_returns_empty_and_does_not_throw()
    {
        // Never throws on broken input.
        var defs = await Analyzer().GetDefinitionsAsync(Request("(((###$$$ not c# @@@"), CancellationToken.None);
        var lines = await Analyzer().ConfirmReferenceLinesAsync(Request("(((###$$$"), "x", CancellationToken.None);

        Assert.Empty(defs);
        Assert.Empty(lines);
    }

    [Fact]
    public async Task Oversized_source_skips_parsing_and_returns_empty()
    {
        // Source over MaxStructuralParseBytes is not parsed.
        var tiny = new AiReviewOptions { MaxStructuralParseBytes = 8 };
        var lines = await Analyzer(tiny).ConfirmReferenceLinesAsync(Request(FixtureSource), "CalculateTotal", CancellationToken.None);

        Assert.Empty(lines);
    }
}
