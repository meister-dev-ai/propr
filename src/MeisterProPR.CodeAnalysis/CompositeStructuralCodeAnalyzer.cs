// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.CodeAnalysis;

/// <summary>
///     The language-agnostic router consumers resolve as <see cref="IStructuralCodeAnalyzer" />.
///     Holds the registered backends (Tree-sitter for the seven, Roslyn-syntax for C#) and
///     dispatches every call to the first backend whose <see cref="IStructuralCodeAnalyzer.CanAnalyze" />
///     is true for the file's path. Returns empty when no backend matches.
/// </summary>
public sealed class CompositeStructuralCodeAnalyzer : IStructuralCodeAnalyzer
{
    private readonly IReadOnlyList<IStructuralCodeAnalyzer> _backends;

    /// <summary>
    ///     Creates the composite over the supplied backends, in priority order (first match wins).
    /// </summary>
    public CompositeStructuralCodeAnalyzer(IEnumerable<IStructuralCodeAnalyzer> backends)
    {
        ArgumentNullException.ThrowIfNull(backends);
        this._backends = backends.ToArray();
    }

    /// <summary>True when at least one backend is available.</summary>
    public bool IsAvailable => this._backends.Any(static b => b.IsAvailable);

    /// <inheritdoc />
    public bool CanAnalyze(string path)
    {
        return this.Select(path) is not null;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<EnclosingDefinition>> ResolveEnclosingDefinitionsAsync(
        StructuralParseRequest request,
        CancellationToken ct)
    {
        var backend = this.Select(request.Path);
        return backend is null
            ? Task.FromResult<IReadOnlyList<EnclosingDefinition>>([])
            : backend.ResolveEnclosingDefinitionsAsync(request, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DefinitionSummary>> GetDefinitionsAsync(
        StructuralParseRequest request,
        CancellationToken ct)
    {
        var backend = this.Select(request.Path);
        return backend is null
            ? Task.FromResult<IReadOnlyList<DefinitionSummary>>([])
            : backend.GetDefinitionsAsync(request, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<int>> ConfirmReferenceLinesAsync(
        StructuralParseRequest request,
        string symbol,
        CancellationToken ct)
    {
        var backend = this.Select(request.Path);
        return backend is null
            ? Task.FromResult<IReadOnlyList<int>>([])
            : backend.ConfirmReferenceLinesAsync(request, symbol, ct);
    }

    /// <inheritdoc />
    public Task<string> ExtractCodeTextAsync(
        StructuralParseRequest request,
        CancellationToken ct)
    {
        var backend = this.Select(request.Path);
        return backend is null
            ? Task.FromResult(string.Empty)
            : backend.ExtractCodeTextAsync(request, ct);
    }

    private IStructuralCodeAnalyzer? Select(string path)
    {
        foreach (var backend in this._backends)
        {
            if (backend.CanAnalyze(path))
            {
                return backend;
            }
        }

        return null;
    }
}
