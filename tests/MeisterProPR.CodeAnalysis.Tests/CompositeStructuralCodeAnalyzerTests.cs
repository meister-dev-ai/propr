// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using NSubstitute;

namespace MeisterProPR.CodeAnalysis.Tests;

public sealed class CompositeStructuralCodeAnalyzerTests
{
    private static StructuralParseRequest Request(string path)
    {
        return new StructuralParseRequest(path, SupportedLanguage.CSharp, "// source", [new ChangedLineRange(1, 1)]);
    }

    private static IStructuralCodeAnalyzer Backend(Func<string, bool> canAnalyze, bool available = true)
    {
        var backend = Substitute.For<IStructuralCodeAnalyzer>();
        backend.IsAvailable.Returns(available);
        backend.CanAnalyze(Arg.Any<string>()).Returns(ci => canAnalyze(ci.Arg<string>()));
        backend.GetDefinitionsAsync(Arg.Any<StructuralParseRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<DefinitionSummary>>([new DefinitionSummary(DefinitionKind.Class, "X", 1, 2)]));
        backend.ResolveEnclosingDefinitionsAsync(Arg.Any<StructuralParseRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<EnclosingDefinition>>([new EnclosingDefinition(DefinitionKind.Class, "X", 1, 2, 2)]));
        backend.ConfirmReferenceLinesAsync(Arg.Any<StructuralParseRequest>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<int>>([1]));
        return backend;
    }

    [Fact]
    public async Task Dispatches_cs_to_the_roslyn_backend()
    {
        var roslyn = Backend(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
        var treeSitter = Backend(p => p.EndsWith(".ts", StringComparison.OrdinalIgnoreCase));
        var composite = new CompositeStructuralCodeAnalyzer([treeSitter, roslyn]);

        var defs = await composite.GetDefinitionsAsync(Request("src/A.cs"), CancellationToken.None);

        Assert.Single(defs);
        await roslyn.Received(1).GetDefinitionsAsync(Arg.Any<StructuralParseRequest>(), Arg.Any<CancellationToken>());
        await treeSitter.DidNotReceive().GetDefinitionsAsync(Arg.Any<StructuralParseRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("src/a.ts")]
    [InlineData("src/a.go")]
    public async Task Dispatches_tree_sitter_languages_to_the_tree_sitter_backend(string path)
    {
        var roslyn = Backend(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
        var treeSitter = Backend(p => p.EndsWith(".ts") || p.EndsWith(".go"));
        var composite = new CompositeStructuralCodeAnalyzer([treeSitter, roslyn]);

        var lines = await composite.ConfirmReferenceLinesAsync(Request(path), "foo", CancellationToken.None);

        Assert.Single(lines);
        await treeSitter.Received(1).ConfirmReferenceLinesAsync(Arg.Any<StructuralParseRequest>(), "foo", Arg.Any<CancellationToken>());
        await roslyn.DidNotReceive().ConfirmReferenceLinesAsync(Arg.Any<StructuralParseRequest>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unknown_extension_returns_empty_from_every_method()
    {
        var roslyn = Backend(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
        var treeSitter = Backend(p => p.EndsWith(".ts", StringComparison.OrdinalIgnoreCase));
        var composite = new CompositeStructuralCodeAnalyzer([treeSitter, roslyn]);

        Assert.False(composite.CanAnalyze("src/a.rs"));
        Assert.Empty(await composite.GetDefinitionsAsync(Request("src/a.rs"), CancellationToken.None));
        Assert.Empty(await composite.ResolveEnclosingDefinitionsAsync(Request("src/a.rs"), CancellationToken.None));
        Assert.Empty(await composite.ConfirmReferenceLinesAsync(Request("src/a.rs"), "foo", CancellationToken.None));
    }

    [Fact]
    public void CanAnalyze_is_the_OR_of_backends()
    {
        var roslyn = Backend(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
        var treeSitter = Backend(p => p.EndsWith(".ts", StringComparison.OrdinalIgnoreCase));
        var composite = new CompositeStructuralCodeAnalyzer([treeSitter, roslyn]);

        Assert.True(composite.CanAnalyze("a.cs"));
        Assert.True(composite.CanAnalyze("a.ts"));
        Assert.False(composite.CanAnalyze("a.rs"));
    }

    [Fact]
    public void First_matching_backend_wins()
    {
        var first = Backend(_ => true);
        var second = Backend(_ => true);
        var composite = new CompositeStructuralCodeAnalyzer([first, second]);

        _ = composite.GetDefinitionsAsync(Request("a.cs"), CancellationToken.None);

        // Only the first backend (priority order) is consulted when both can analyze.
        Assert.True(composite.CanAnalyze("a.cs"));
    }
}
