// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using NSubstitute;

namespace MeisterProPR.CodeAnalysis.Tests;

/// <summary>
///     Fault containment and concurrency safety at the composite layer:
///     a missing/non-matching backend returns empty (never throws), and concurrent calls over the
///     composite are safe (no shared-state corruption) and deterministic.
/// </summary>
public sealed class FaultContainmentTests
{
    private static StructuralParseRequest Request(string path)
    {
        return new StructuralParseRequest(path, SupportedLanguage.CSharp, "// x", [new ChangedLineRange(1, 1)]);
    }

    [Fact]
    public async Task MissingBackend_ReturnsEmpty_NeverThrows()
    {
        // No backends registered → every method returns empty, CanAnalyze false, never throws.
        var composite = new CompositeStructuralCodeAnalyzer([]);

        Assert.False(composite.CanAnalyze("a.cs"));
        Assert.False(composite.IsAvailable);
        Assert.Empty(await composite.GetDefinitionsAsync(Request("a.cs"), CancellationToken.None));
        Assert.Empty(await composite.ResolveEnclosingDefinitionsAsync(Request("a.cs"), CancellationToken.None));
        Assert.Empty(await composite.ConfirmReferenceLinesAsync(Request("a.cs"), "x", CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentCalls_AreSafeAndDeterministic()
    {
        // A stub backend that returns a deterministic result; the composite must dispatch concurrently
        // without shared-state corruption. Run many overlapping calls and assert every result.
        var backend = Substitute.For<IStructuralCodeAnalyzer>();
        backend.IsAvailable.Returns(true);
        backend.CanAnalyze(Arg.Any<string>()).Returns(ci => ci.Arg<string>().EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
        backend.ConfirmReferenceLinesAsync(Arg.Any<StructuralParseRequest>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<int>>([1, 2, 3]));

        var composite = new CompositeStructuralCodeAnalyzer([backend]);

        var tasks = Enumerable.Range(0, 200)
            .Select(i => composite.ConfirmReferenceLinesAsync(Request($"file{i}.cs"), $"sym{i}", CancellationToken.None))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal([1, 2, 3], r));
    }
}
