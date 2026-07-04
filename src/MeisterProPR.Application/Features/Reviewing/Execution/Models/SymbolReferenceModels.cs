// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.CodeAnalysis;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     A name-based cross-file lookup request for the <c>find_references</c> / <c>get_definition</c>
///     review tools.
/// </summary>
/// <param name="Symbol">The symbol name to resolve.</param>
/// <param name="KindHint">Optional definition-kind hint (advisory; the floor is name-based).</param>
/// <param name="BranchSide">Logical branch side to scan; defaults to <c>source</c>.</param>
public sealed record SymbolReferenceQuery(
    string Symbol,
    DefinitionKind? KindHint = null,
    string BranchSide = "source");

/// <summary>
///     Bounded result of a <c>find_references</c> lookup. Sites exclude comment/string
///     occurrences (confirmed by the language backend). <see cref="Truncated" /> is set when a budget
///     cap was hit; <see cref="Unavailable" /> when the feature/analyzer/workspace was unavailable.
/// </summary>
public sealed record ReferenceLookupResult(
    IReadOnlyList<ReferenceSite> Sites,
    int CandidateFilesScanned,
    bool Truncated,
    bool Unavailable)
{
    /// <summary>An empty, available result.</summary>
    public static ReferenceLookupResult Empty { get; } = new([], 0, false, false);

    /// <summary>An unavailable result (kill-switch off, no analyzer, or workspace unavailable).</summary>
    public static ReferenceLookupResult UnavailableResult { get; } = new([], 0, false, true);
}

/// <summary>
///     A confirmed definition site for <c>get_definition</c>. Line spans are 1-based.
/// </summary>
/// <param name="FilePath">Repo-relative path of the file containing the definition.</param>
/// <param name="Kind">Coarse kind of the definition.</param>
/// <param name="Name">The definition name, or <c>null</c> for anonymous nodes.</param>
/// <param name="StartLine">1-based inclusive start line.</param>
/// <param name="EndLine">1-based inclusive end line.</param>
/// <param name="ResolutionMode">Whether the match is name-based or semantically resolved.</param>
/// <param name="LineSnippet">
///     The declaration line, whitespace-collapsed to a single line and bounded to a small cap, so the
///     caller can see the definition without re-fetching the file. <c>null</c> when unavailable.
/// </param>
public sealed record DefinitionLookupSite(
    string FilePath,
    DefinitionKind Kind,
    string? Name,
    int StartLine,
    int EndLine,
    ResolutionMode ResolutionMode,
    string? LineSnippet = null);

/// <summary>
///     Bounded result of a <c>get_definition</c> lookup.
/// </summary>
public sealed record DefinitionLookupResult(
    IReadOnlyList<DefinitionLookupSite> Definitions,
    int CandidateFilesScanned,
    bool Truncated,
    bool Unavailable)
{
    /// <summary>An empty, available result.</summary>
    public static DefinitionLookupResult Empty { get; } = new([], 0, false, false);

    /// <summary>An unavailable result.</summary>
    public static DefinitionLookupResult UnavailableResult { get; } = new([], 0, false, true);
}
