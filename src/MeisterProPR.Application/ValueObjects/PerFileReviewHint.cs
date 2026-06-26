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

    /// <summary>
    ///     Deterministically prefetched surrounding-code and caller evidence injected before the file review runs.
    /// </summary>
    public IReadOnlyList<PrefetchedContextEvidenceItem> PrefetchedContextEvidence { get; init; } = [];

    /// <summary>
    ///     Deterministic security risk markers extracted from the current diff.
    /// </summary>
    public FileRiskMarkers RiskMarkers { get; init; } = FileRiskMarkers.None;

    /// <summary>
    ///     Deterministic blast-radius signal: how many confirmed references the file's changed symbols have.
    ///     Three-state (Measured / Truncated / Unavailable); feeds the complexity-tier triage decision.
    /// </summary>
    public FanOutSignal FanOut { get; init; } = FanOutSignal.Unavailable;
}

/// <summary>
///     One deterministic prefetch evidence item captured before the per-file review starts.
/// </summary>
/// <param name="Kind">Machine-readable evidence kind.</param>
/// <param name="Title">Short display title for the prompt.</param>
/// <param name="SourceId">Stable source identifier such as a path or symbol key.</param>
/// <param name="Content">Bounded evidence content rendered into the prompt.</param>
/// <param name="Truncated">Whether the content was trimmed to stay within the budget.</param>
public sealed record PrefetchedContextEvidenceItem(
    string Kind,
    string Title,
    string SourceId,
    string Content,
    bool Truncated = false);

/// <summary>
///     Deterministic risk markers extracted from a changed file diff.
/// </summary>
public sealed record FileRiskMarkers(
    bool HasSecurityMarkers,
    IReadOnlyList<string> MatchedMarkers)
{
    public static FileRiskMarkers None { get; } = new(false, []);

    public bool HasAnyMarkers => this.HasSecurityMarkers;
}

/// <summary>
///     Kind of deterministic blast-radius (fan-out) measurement for a changed file.
/// </summary>
public enum FanOutKind
{
    /// <summary>No structural data — non-parseable file, new symbol, or the resolver was unavailable. Never read as zero.</summary>
    Unavailable,

    /// <summary>A real reference count (which may legitimately be zero).</summary>
    Measured,

    /// <summary>The reference resolver capped out; <see cref="FanOutSignal.Count" /> is a lower bound and impact is treated as high.</summary>
    Truncated,
}

/// <summary>
///     Deterministic blast-radius signal for a changed file: the number of confirmed references to its
///     changed symbols. <see cref="FanOutKind.Unavailable" /> is distinct from a measured zero.
/// </summary>
public sealed record FanOutSignal(FanOutKind Kind, int Count)
{
    public static FanOutSignal Unavailable { get; } = new(FanOutKind.Unavailable, 0);

    /// <summary>True when a structural measurement was obtained (Measured or Truncated).</summary>
    public bool HasData => this.Kind != FanOutKind.Unavailable;

    public static FanOutSignal Measured(int count)
    {
        return new FanOutSignal(FanOutKind.Measured, Math.Max(0, count));
    }

    public static FanOutSignal Truncated(int lowerBound)
    {
        return new FanOutSignal(FanOutKind.Truncated, Math.Max(0, lowerBound));
    }
}
