// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.ValueObjects;

/// <summary>
///     Carries per-file review framing metadata for the AI review core.
///     When <see cref="ReviewSystemContext.PerFileHint" /> is non-null,
///     <c>ToolAwareAiReviewCore</c> uses <c>BuildPerFileSystemPrompt</c> and
///     <c>BuildPerFileUserMessage</c> instead of the whole-PR prompt builders.
/// </summary>
/// <param name="FilePath">The path of the file currently under review.</param>
/// <param name="FileIndex">1-based index of this file among files being reviewed in this pass.</param>
/// <param name="TotalFiles">Total number of files being reviewed in this pass (delta count).</param>
/// <param name="AllChangedFileSummaries">
///     Complete manifest of all files in the PR (path + change type).
///     On re-review passes this is larger than <paramref name="TotalFiles" /> and provides
///     context beyond the delta being actively reviewed.
/// </param>
public sealed record PerFileReviewHint(
    string FilePath,
    int FileIndex,
    int TotalFiles,
    IReadOnlyList<ChangedFileSummary> AllChangedFileSummaries)
{
    /// <summary>Complexity tier derived from changed-line count. Determines the max iteration budget.</summary>
    public FileComplexityTier ComplexityTier { get; init; } = FileComplexityTier.Medium;

    /// <summary>
    ///     When set, overrides <c>AiReviewOptions.MaxIterations</c> for this specific file.
    ///     Derived from the per-tier iteration limits based on <see cref="ComplexityTier" />.
    /// </summary>
    public int? MaxIterationsOverride { get; init; }
}
