// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.CodeAnalysis;

/// <summary>
///     Input to <see cref="IStructuralCodeAnalyzer" />. The caller supplies the source text
///     (read from <c>IReviewRepositoryWorkspace</c>); the analyzer never fetches files.
/// </summary>
/// <param name="Path">Repo-relative path, used only for diagnostics and language inference fallback.</param>
/// <param name="Language">Resolved <see cref="SupportedLanguage" /> for the file.</param>
/// <param name="SourceText">UTF-8 source text of the file. Non-empty for a valid request.</param>
/// <param name="ChangedLineRanges">1-based inclusive changed line ranges in the new file.</param>
public sealed record StructuralParseRequest(
    string Path,
    SupportedLanguage Language,
    string SourceText,
    IReadOnlyList<ChangedLineRange> ChangedLineRanges);
