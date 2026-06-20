// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.CodeAnalysis;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Direct-invocation tests for the <c>get_definition</c> tool.
/// </summary>
public sealed class GetDefinitionToolTests
{
    // Defining site(s) for a symbol defined elsewhere (C# via the Roslyn backend).
    [Fact]
    public async Task GetDefinition_ReturnsDefiningSite_ForCSharpSymbol()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Calc.cs"] = "namespace N;\npublic class Calc\n{\n    public int CalcTotal() => 1;\n}\n",
            ["User.cs"] = "namespace N;\npublic class User\n{\n    public int Use(Calc c) => c.CalcTotal();\n}\n",
        };

        var tools = StructuralReferenceToolTestHarness.CreateTools(files);
        var result = await tools.GetDefinitionAsync(new SymbolReferenceQuery("CalcTotal"), CancellationToken.None);

        Assert.False(result.Unavailable);
        Assert.Contains(
            result.Definitions, d =>
                d.FilePath == "Calc.cs" && d.Name == "CalcTotal" && d.Kind == DefinitionKind.Method);
        Assert.All(result.Definitions, d => Assert.Equal(ResolutionMode.NameBased, d.ResolutionMode));
    }

    // An ambiguous name → all syntactic candidates (name-based).
    [Fact]
    public async Task GetDefinition_AmbiguousName_ReturnsAllCandidates()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["A.cs"] = "namespace N;\npublic class A\n{\n    public int Run() => 1;\n}\n",
            ["B.cs"] = "namespace N;\npublic class B\n{\n    public int Run() => 2;\n}\n",
        };

        var tools = StructuralReferenceToolTestHarness.CreateTools(files);
        var result = await tools.GetDefinitionAsync(new SymbolReferenceQuery("Run"), CancellationToken.None);

        Assert.Contains(result.Definitions, d => d.FilePath == "A.cs");
        Assert.Contains(result.Definitions, d => d.FilePath == "B.cs");
        Assert.True(result.Definitions.Count >= 2);
    }

    // No definition found → empty.
    [Fact]
    public async Task GetDefinition_NoMatch_ReturnsEmpty()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["A.cs"] = "namespace N;\npublic class A { public int Run() => 1; }\n",
        };

        var tools = StructuralReferenceToolTestHarness.CreateTools(files);
        var result = await tools.GetDefinitionAsync(new SymbolReferenceQuery("Missing"), CancellationToken.None);

        Assert.False(result.Unavailable);
        Assert.Empty(result.Definitions);
    }

    [Fact]
    public async Task GetDefinition_KillSwitchOff_ReturnsUnavailable()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal) { ["A.cs"] = "class A {}\n" };
        var options = StructuralReferenceToolTestHarness.DefaultOptions();
        options.EnableStructuralReferenceTools = false;

        var tools = StructuralReferenceToolTestHarness.CreateTools(files, options);
        var result = await tools.GetDefinitionAsync(new SymbolReferenceQuery("A"), CancellationToken.None);

        Assert.True(result.Unavailable);
    }
}
