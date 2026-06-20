// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.CodeAnalysis;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Observed, non-gating metrics:
///     <list type="bullet">
///         <item>
///             the rate at which the agent spontaneously invokes the reference/definition tools,
///             derived from trace tool-call names. Tracked over time; not a release gate.
///         </item>
///         <item>
///             comment/string-exclusion precision of the structural confirmation over a small
///             labeled sample.
///         </item>
///     </list>
/// </summary>
public sealed class StructuralReferenceMetricsTests
{
    private static readonly string[] StructuralToolNames = ["find_references", "get_definition"];

    /// <summary>Reporting helper: fraction of review passes that invoked a structural reference tool.</summary>
    private static double SpontaneousAdoptionRate(IReadOnlyList<IReadOnlyList<string>> perPassToolCalls)
    {
        if (perPassToolCalls.Count == 0)
        {
            return 0d;
        }

        var passesUsingTool = perPassToolCalls.Count(pass =>
            pass.Any(name => StructuralToolNames.Contains(name, StringComparer.Ordinal)));
        return (double)passesUsingTool / perPassToolCalls.Count;
    }

    [Fact]
    public void AdoptionRate_ComputedFromTraceToolCalls()
    {
        // Simulated per-pass tool-call traces: 2 of 4 passes invoked a structural tool.
        IReadOnlyList<IReadOnlyList<string>> traces =
        [
            ["get_file_content", "find_references"],
            ["search_code"],
            ["get_definition", "get_file_content"],
            ["get_changed_files"],
        ];

        var rate = SpontaneousAdoptionRate(traces);

        Assert.Equal(0.5d, rate, 3);
    }

    [Fact]
    public async Task CommentStringExclusionPrecision_MeetsThreshold()
    {
        var analyzer = FileByFileContextPrefetchStageTests.CreateRoslynAnalyzer();

        // Labeled sample: `Target` appears on 3 real-code lines and 2 comment/string lines.
        const string source =
            "namespace N;\n" + // 1
            "public class C\n" + // 2
            "{\n" + // 3
            "    public int Target() => 1;\n" + // 4  real (definition)
            "    public int A() => this.Target();\n" + // 5  real (call)
            "    public int B() => this.Target() + 1;\n" + // 6  real (call)
            "    // Target mentioned in a comment\n" + // 7  comment (must be excluded)
            "    public string S() => \"Target literal\";\n" + // 8  string (must be excluded)
            "}\n";
        var realCodeLines = new HashSet<int> { 4, 5, 6 };

        var request = new StructuralParseRequest("C.cs", SupportedLanguage.CSharp, source, []);
        var confirmed = await analyzer.ConfirmReferenceLinesAsync(request, "Target", CancellationToken.None);

        Assert.NotEmpty(confirmed);
        var truePositives = confirmed.Count(realCodeLines.Contains);
        var precision = (double)truePositives / confirmed.Count;

        // Target: >= 0.95 of confirmed sites are real code occurrences (comment/string excluded).
        Assert.True(precision >= 0.95d, $"Structural-confirmation precision {precision:P0} is below the 95% target.");
    }
}
