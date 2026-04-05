// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.ObjectModel;

namespace MeisterProPR.Application.DTOs;

/// <summary>
///     Aggregate diagnostics captured while evaluating review findings for final PR comment posting.
/// </summary>
public sealed record ReviewCommentPostingDiagnosticsDto
{
    private static readonly IReadOnlyDictionary<string, int> EmptyReasonCounts =
        new ReadOnlyDictionary<string, int>(new Dictionary<string, int>());

    /// <summary>Total candidate findings evaluated for posting.</summary>
    public int CandidateCount { get; init; }

    /// <summary>Candidate findings that remained postable after duplicate suppression.</summary>
    public int PostedCount { get; init; }

    /// <summary>Candidate findings suppressed as duplicates or carried-forward artifacts.</summary>
    public int SuppressedCount { get; init; }

    /// <summary>Count of candidates skipped because they originated from carried-forward file results.</summary>
    public int CarriedForwardCandidatesSkipped { get; init; }

    /// <summary>Canonical suppression-reason counts keyed by reason code.</summary>
    public IReadOnlyDictionary<string, int> SuppressionReasons { get; init; } = EmptyReasonCounts;

    /// <summary>Whether active/open bot-authored threads were part of the duplicate-check corpus.</summary>
    public bool ConsideredOpenThreads { get; init; }

    /// <summary>Whether resolved bot-authored threads were part of the duplicate-check corpus.</summary>
    public bool ConsideredResolvedThreads { get; init; }

    /// <summary>Fallback duplicate checks that were used because full historical protection was unavailable.</summary>
    public IReadOnlyList<string> FallbackChecks { get; init; } = [];

    /// <summary>Named duplicate-protection components that were unavailable during evaluation.</summary>
    public IReadOnlyList<string> DegradedComponents { get; init; } = [];

    /// <summary>Human-readable cause describing why duplicate protection ran in degraded mode.</summary>
    public string? DegradedCause { get; init; }

    /// <summary>Number of candidates evaluated while degraded duplicate protection was active.</summary>
    public int AffectedCandidateCount { get; init; }

    /// <summary>True when any reduced duplicate checks were used during the posting pass.</summary>
    public bool UsedFallbackChecks => this.FallbackChecks.Count > 0;

    /// <summary>True when any duplicate-protection component was unavailable during the posting pass.</summary>
    public bool IsDegraded => this.DegradedComponents.Count > 0;

    /// <summary>Returns an empty diagnostics object for callers that do not need duplicate-suppression metadata.</summary>
    public static ReviewCommentPostingDiagnosticsDto Empty(
        int candidateCount = 0,
        int carriedForwardCandidatesSkipped = 0)
    {
        return new ReviewCommentPostingDiagnosticsDto
        {
            CandidateCount = candidateCount,
            PostedCount = candidateCount - carriedForwardCandidatesSkipped,
            SuppressedCount = carriedForwardCandidatesSkipped,
            CarriedForwardCandidatesSkipped = carriedForwardCandidatesSkipped,
            SuppressionReasons = carriedForwardCandidatesSkipped > 0
                ? new ReadOnlyDictionary<string, int>(
                    new Dictionary<string, int>
                    {
                        ["carried_forward_source"] = carriedForwardCandidatesSkipped,
                    })
                : EmptyReasonCounts,
        };
    }
}

/// <summary>
///     Result returned by the historical thread-memory duplicate-suppression lookup.
/// </summary>
public sealed record HistoricalDuplicateSuppressionMatchDto
{
    /// <summary>True when the current finding matched a historical duplicate candidate.</summary>
    public bool IsDuplicate { get; init; }

    /// <summary>Canonical reason code describing why the finding was suppressed.</summary>
    public string? ReasonCode { get; init; }

    /// <summary>Historical thread identifier that triggered the match, when available.</summary>
    public int? ThreadId { get; init; }

    /// <summary>Historical memory-record identifier that triggered the match, when available.</summary>
    public Guid? MemoryRecordId { get; init; }

    /// <summary>Similarity score associated with the historical match, when available.</summary>
    public float? SimilarityScore { get; init; }

    /// <summary>Named duplicate-protection components that were unavailable during the lookup.</summary>
    public IReadOnlyList<string> DegradedComponents { get; init; } = [];

    /// <summary>Fallback checks that were used because the preferred historical lookup path was unavailable.</summary>
    public IReadOnlyList<string> FallbackChecks { get; init; } = [];

    /// <summary>Human-readable cause describing why historical matching was degraded.</summary>
    public string? DegradedCause { get; init; }

    /// <summary>True when any historical duplicate-protection component was unavailable.</summary>
    public bool IsDegraded => this.DegradedComponents.Count > 0;

    /// <summary>True when the lookup relied on reduced duplicate-protection checks.</summary>
    public bool UsedFallbackChecks => this.FallbackChecks.Count > 0;

    /// <summary>Returns a non-matching result while preserving degraded-mode metadata.</summary>
    public static HistoricalDuplicateSuppressionMatchDto NoMatch(
        IReadOnlyList<string>? degradedComponents = null,
        string? degradedCause = null,
        IReadOnlyList<string>? fallbackChecks = null)
    {
        return new HistoricalDuplicateSuppressionMatchDto
        {
            DegradedComponents = degradedComponents ?? [],
            DegradedCause = degradedCause,
            FallbackChecks = fallbackChecks ?? [],
        };
    }

    /// <summary>Returns a duplicate match for the current candidate finding.</summary>
    public static HistoricalDuplicateSuppressionMatchDto Match(
        string reasonCode,
        int? threadId = null,
        Guid? memoryRecordId = null,
        float? similarityScore = null,
        IReadOnlyList<string>? degradedComponents = null,
        string? degradedCause = null,
        IReadOnlyList<string>? fallbackChecks = null)
    {
        return new HistoricalDuplicateSuppressionMatchDto
        {
            IsDuplicate = true,
            ReasonCode = reasonCode,
            ThreadId = threadId,
            MemoryRecordId = memoryRecordId,
            SimilarityScore = similarityScore,
            DegradedComponents = degradedComponents ?? [],
            DegradedCause = degradedCause,
            FallbackChecks = fallbackChecks ?? [],
        };
    }
}
