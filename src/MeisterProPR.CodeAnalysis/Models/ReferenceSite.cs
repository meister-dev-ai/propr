// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.CodeAnalysis;

/// <summary>
///     The kind of occurrence a confirmed reference site represents.
/// </summary>
public enum OccurrenceKind
{
    /// <summary>The symbol is invoked (call expression).</summary>
    Call,

    /// <summary>The symbol is referenced as an identifier (non-call use).</summary>
    Reference,
}

/// <summary>
///     A confirmed cross-file usage occurrence of a symbol — a real identifier/reference node,
///     with comment and string/literal matches excluded by the backend. Line is 1-based.
/// </summary>
/// <param name="FilePath">Repo-relative path of the file containing the occurrence.</param>
/// <param name="Line">1-based line of the occurrence.</param>
/// <param name="EnclosingName">Name of the enclosing definition, when resolvable.</param>
/// <param name="EnclosingKind">Kind of the enclosing definition, when resolvable.</param>
/// <param name="OccurrenceKind">Whether this is a call or a plain reference.</param>
/// <param name="ResolutionMode">Whether the match is name-based or semantically resolved.</param>
public sealed record ReferenceSite(
    string FilePath,
    int Line,
    string? EnclosingName,
    DefinitionKind? EnclosingKind,
    OccurrenceKind OccurrenceKind,
    ResolutionMode ResolutionMode);
