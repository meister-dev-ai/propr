// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.ValueObjects;

/// <summary>
///     One focused review-guidance item injected into a per-file review prompt.
/// </summary>
/// <param name="Id">Stable guidance identifier.</param>
/// <param name="Title">Human-readable title.</param>
/// <param name="ShortDescription">Compact description of the concern.</param>
/// <param name="Instruction">Detailed what-to-look-for guidance.</param>
/// <param name="Reason">Short diff-grounded reason from the prefilter stage.</param>
/// <param name="Score">Relative ranking score from 0 to 100.</param>
public sealed record FocusedReviewGuidanceItem(
    string Id,
    string Title,
    string ShortDescription,
    string Instruction,
    string Reason,
    int Score);

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

    /// <summary>
    ///     Optional focused review guidance generated from ProRV's diff-based prefilter stage.
    /// </summary>
    public IReadOnlyList<FocusedReviewGuidanceItem> FocusedReviewGuidance { get; init; } = [];

    /// <summary>
    ///     Optional Stage A plan for agentic file-by-file review mode.
    /// </summary>
    public AgenticFileReviewPlan? AgenticPlan { get; init; }

    /// <summary>
    ///     Optional bounded Stage B investigation outputs for the current file.
    /// </summary>
    public IReadOnlyList<AgenticFileInvestigationResult> AgenticInvestigations { get; init; } = [];

    /// <summary>
    ///     Surviving agentic candidate findings after local verification, preserving provenance and support metadata.
    /// </summary>
    public IReadOnlyList<CandidateReviewFinding> VerifiedAgenticCandidateFindings { get; init; } = [];
}
