// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.CodeAnalysis;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Direct-invocation tests for the <c>find_references</c> tool. Confirmed
///     sites exclude comment/string matches; results are bounded; unavailable paths never throw.
/// </summary>
public sealed class FindReferencesToolTests
{
    // A symbol with real callers in other files → confirmed sites (file+line); comment/string excluded.
    [Fact]
    public async Task FindReferences_ReturnsConfirmedSites_ExcludingCommentAndString()
    {
        if (!StructuralReferenceToolTestHarness.TreeSitterAvailable)
        {
            return; // Native libs unavailable - the analyzer suite covers Tree-sitter confirmation directly.
        }

        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a.ts"] = "export function targetSym() { return 1; }\n",
            ["b.ts"] = "import { targetSym } from './a';\n" + // line 1 - reference
                       "const x = targetSym();\n" + // line 2 - real call
                       "// targetSym usage note\n" + // line 3 - comment (excluded)
                       "const s = \"targetSym\";\n", // line 4 - string (excluded)
        };

        var tools = StructuralReferenceToolTestHarness.CreateTools(files);
        var result = await tools.FindReferencesAsync(new SymbolReferenceQuery("targetSym"), CancellationToken.None);

        Assert.False(result.Unavailable);
        Assert.Contains(result.Sites, s => s.FilePath == "b.ts" && s.Line == 2);
        Assert.DoesNotContain(result.Sites, s => s.FilePath == "b.ts" && s.Line == 3);
        Assert.DoesNotContain(result.Sites, s => s.FilePath == "b.ts" && s.Line == 4);
        Assert.All(result.Sites, s => Assert.Equal(ResolutionMode.NameBased, s.ResolutionMode));
        Assert.True(result.CandidateFilesScanned >= 2);
    }

    // A symbol with no references → empty (not an error).
    [Fact]
    public async Task FindReferences_NoMatches_ReturnsEmptyNotError()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a.cs"] = "namespace N; public class C { public int M() => 1; }\n",
        };

        var tools = StructuralReferenceToolTestHarness.CreateTools(files);
        var result = await tools.FindReferencesAsync(new SymbolReferenceQuery("DoesNotExist"), CancellationToken.None);

        Assert.False(result.Unavailable);
        Assert.Empty(result.Sites);
    }

    // Many candidates → bounded by MaxReferenceResults with truncated=true.
    [Fact]
    public async Task FindReferences_ManyMatches_IsBoundedAndTruncated()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["a.cs"] = "namespace N;\npublic class C\n{\n" +
                       "    public int A() => Helper();\n" +
                       "    public int B() => Helper();\n" +
                       "    public int D() => Helper();\n" +
                       "    public int Helper() => 1;\n" +
                       "}\n",
        };

        var options = StructuralReferenceToolTestHarness.DefaultOptions();
        options.MaxReferenceResults = 1;

        var tools = StructuralReferenceToolTestHarness.CreateTools(files, options);
        var result = await tools.FindReferencesAsync(new SymbolReferenceQuery("Helper"), CancellationToken.None);

        Assert.True(result.Sites.Count <= 1);
        Assert.True(result.Truncated);
    }

    // Kill-switch off → unavailable, no throw, review proceeds.
    [Fact]
    public async Task FindReferences_KillSwitchOff_ReturnsUnavailable()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal) { ["a.cs"] = "class C {}\n" };
        var options = StructuralReferenceToolTestHarness.DefaultOptions();
        options.EnableStructuralReferenceTools = false;

        var tools = StructuralReferenceToolTestHarness.CreateTools(files, options);
        var result = await tools.FindReferencesAsync(new SymbolReferenceQuery("C"), CancellationToken.None);

        Assert.True(result.Unavailable);
        Assert.Empty(result.Sites);
    }

    // No analyzer wired → unavailable, no throw.
    [Fact]
    public async Task FindReferences_NoAnalyzer_ReturnsUnavailable()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal) { ["a.cs"] = "class C {}\n" };
        var tools = StructuralReferenceToolTestHarness.CreateTools(files, includeAnalyzer: false);

        var result = await tools.FindReferencesAsync(new SymbolReferenceQuery("C"), CancellationToken.None);

        Assert.True(result.Unavailable);
    }

    // A C# symbol → served via the Roslyn-syntax backend; resolutionMode = name-based.
    [Fact]
    public async Task FindReferences_CSharpSymbol_ServedViaRoslyn()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Calc.cs"] = "namespace N;\npublic class Calc\n{\n" +
                          "    public int CalcX() => 1;\n" +
                          "    public int Use() => this.CalcX();\n" + // line 5 - real call
                          "    // CalcX in a comment\n" + // line 6 - comment
                          "    public string S() => \"CalcX\";\n" + // line 7 - string
                          "}\n",
        };

        var tools = StructuralReferenceToolTestHarness.CreateTools(files);
        var result = await tools.FindReferencesAsync(new SymbolReferenceQuery("CalcX"), CancellationToken.None);

        Assert.False(result.Unavailable);
        Assert.Contains(result.Sites, s => s.FilePath == "Calc.cs" && s.Line == 5);
        Assert.DoesNotContain(result.Sites, s => s.Line == 6); // comment excluded
        Assert.DoesNotContain(result.Sites, s => s.Line == 7); // string excluded
        Assert.All(result.Sites, s => Assert.Equal(ResolutionMode.NameBased, s.ResolutionMode));
    }
}
