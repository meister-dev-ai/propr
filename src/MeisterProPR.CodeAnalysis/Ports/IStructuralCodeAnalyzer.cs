// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.CodeAnalysis;

/// <summary>
///     The unified, all-language code-analysis abstraction. Answers structure questions
///     (definitions, the enclosing definition for a changed range, and confirmed cross-file
///     references) for every supported language behind one contract.
/// </summary>
/// <remarks>
///     <para>
///         Backends implement this port and declare <see cref="CanAnalyze" />: the Tree-sitter
///         backend (TS/TSX/JS/Py/Go/Java/Ruby, syntactic) in <c>MeisterProPR.CodeAnalysis.TreeSitter</c>
///         and the Roslyn-syntax backend (C#, syntactic) in <c>MeisterProPR.CodeAnalysis.Roslyn</c>.
///         A <c>CompositeStructuralCodeAnalyzer</c> dispatches by language; consumers depend only
///         on this port.
///     </para>
///     <para>
///         Invariants:
///         <list type="bullet">
///             <item>The analyzer never fetches files; the caller supplies <see cref="StructuralParseRequest.SourceText" />.</item>
///             <item>
///                 The analyzer never throws to the caller for input/parse problems — it returns empty so
///                 consumers fall back deterministically.
///             </item>
///             <item>
///                 Name-based (syntactic) floor for every language; <see cref="ResolutionMode" /> labels
///                 whether a result is name-based or semantically resolved.
///             </item>
///         </list>
///     </para>
/// </remarks>
public interface IStructuralCodeAnalyzer
{
    /// <summary>
    ///     True when the backend is usable on the running platform (e.g. a native parser loaded).
    ///     When false, all calls return empty and callers use their heuristic fallback.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    ///     True when this backend can analyze the file's language (by extension) AND <see cref="IsAvailable" />.
    /// </summary>
    /// <param name="path">A repo-relative or absolute file path used only to infer language by extension.</param>
    bool CanAnalyze(string path);

    /// <summary>
    ///     Resolve the enclosing definition for each changed line range.
    /// </summary>
    /// <returns>
    ///     One <see cref="EnclosingDefinition" /> per range (merged when overlapping), or an empty list
    ///     when none. MUST NOT throw on parse fault/timeout/oversize - returns empty and the caller
    ///     falls back to the heuristic.
    /// </returns>
    Task<IReadOnlyList<EnclosingDefinition>> ResolveEnclosingDefinitionsAsync(
        StructuralParseRequest request,
        CancellationToken ct);

    /// <summary>
    ///     List a file's top-level/nested definitions.
    /// </summary>
    /// <returns>MUST NOT throw on parse failure - returns empty.</returns>
    Task<IReadOnlyList<DefinitionSummary>> GetDefinitionsAsync(
        StructuralParseRequest request,
        CancellationToken ct);

    /// <summary>
    ///     Confirm the 1-based lines on which <paramref name="symbol" /> occurs as a real
    ///     identifier/reference node, excluding occurrences inside comments and string/char/
    ///     interpolated literals.
    /// </summary>
    /// <param name="request">The source buffer and resolved language.</param>
    /// <param name="symbol">The symbol name to confirm.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     Distinct 1-based line numbers in ascending order. Empty on
    ///     fault/timeout/oversize/unsupported; MUST NOT throw.
    /// </returns>
    Task<IReadOnlyList<int>> ConfirmReferenceLinesAsync(
        StructuralParseRequest request,
        string symbol,
        CancellationToken ct);

    /// <summary>
    ///     Returns <see cref="StructuralParseRequest.SourceText" /> with every comment and
    ///     string/char/interpolated-literal span blanked (replaced by spaces; newlines preserved so line
    ///     numbers and line count are unchanged), leaving only real code. Lets callers match patterns
    ///     against code without spurious hits inside comments or string literals.
    /// </summary>
    /// <returns>
    ///     The code-only projection of the source. Empty string on fault/timeout/oversize/unsupported or
    ///     when the backend is unavailable; MUST NOT throw.
    /// </returns>
    Task<string> ExtractCodeTextAsync(
        StructuralParseRequest request,
        CancellationToken ct);
}
