// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.ObjectModel;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Full per-file result returned by a comment relevance filter implementation.
/// </summary>
public sealed record CommentRelevanceFilterResult
{
    private static readonly IReadOnlyDictionary<string, int> EmptyCounts =
        new ReadOnlyDictionary<string, int>(new Dictionary<string, int>());

    /// <summary>
    ///     Creates a new <see cref="CommentRelevanceFilterResult" />.
    /// </summary>
    public CommentRelevanceFilterResult(
        string implementationId,
        string implementationVersion,
        string filePath,
        int originalCommentCount,
        IReadOnlyList<CommentRelevanceFilterDecision> decisions,
        IReadOnlyList<string>? degradedComponents = null,
        IReadOnlyList<string>? fallbackChecks = null,
        string? degradedCause = null,
        FilterAiTokenUsage? aiTokenUsage = null)
    {
        if (string.IsNullOrWhiteSpace(implementationId))
        {
            throw new ArgumentException("Implementation identifier is required.", nameof(implementationId));
        }

        if (string.IsNullOrWhiteSpace(implementationVersion))
        {
            throw new ArgumentException("Implementation version is required.", nameof(implementationVersion));
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path is required.", nameof(filePath));
        }

        var copiedDecisions = decisions?.ToArray() ?? throw new ArgumentNullException(nameof(decisions));
        if (copiedDecisions.Length != originalCommentCount)
        {
            throw new ArgumentException("Decision count must match the original comment count.", nameof(decisions));
        }

        this.ImplementationId = implementationId;
        this.ImplementationVersion = implementationVersion;
        this.FilePath = filePath;
        this.OriginalCommentCount = originalCommentCount;
        this.Decisions = copiedDecisions;
        this.KeptCount = copiedDecisions.Count(decision => decision.IsKeep);
        this.DiscardedCount = copiedDecisions.Length - this.KeptCount;
        this.ReasonBuckets = BuildReasonBuckets(copiedDecisions);
        this.DegradedComponents = degradedComponents?.Where(component => !string.IsNullOrWhiteSpace(component)).ToArray() ?? [];
        this.FallbackChecks = fallbackChecks?.Where(check => !string.IsNullOrWhiteSpace(check)).ToArray() ?? [];
        this.DegradedCause = degradedCause;
        this.AiTokenUsage = aiTokenUsage;
    }

    /// <summary>The stable filter implementation identifier.</summary>
    public string ImplementationId { get; }

    /// <summary>The recorded version string for the filter implementation.</summary>
    public string ImplementationVersion { get; }

    /// <summary>The file path reviewed by this filter pass.</summary>
    public string FilePath { get; }

    /// <summary>The number of comment candidates entering the filter.</summary>
    public int OriginalCommentCount { get; }

    /// <summary>The number of comments retained by the filter.</summary>
    public int KeptCount { get; }

    /// <summary>The number of comments discarded by the filter.</summary>
    public int DiscardedCount { get; }

    /// <summary>Aggregate discard counts keyed by reason code.</summary>
    public IReadOnlyDictionary<string, int> ReasonBuckets { get; }

    /// <summary>The ordered per-comment decisions produced by the filter.</summary>
    public IReadOnlyList<CommentRelevanceFilterDecision> Decisions { get; }

    /// <summary>Named components unavailable during this filter pass.</summary>
    public IReadOnlyList<string> DegradedComponents { get; }

    /// <summary>Fallback checks used instead of the preferred filter behavior.</summary>
    public IReadOnlyList<string> FallbackChecks { get; }

    /// <summary>Human-readable degraded-mode cause for the filter pass.</summary>
    public string? DegradedCause { get; }

    /// <summary>Token usage attributed to filter AI work when present.</summary>
    public FilterAiTokenUsage? AiTokenUsage { get; }

    /// <summary>
    ///     Returns the kept comments in their original order.
    /// </summary>
    public IReadOnlyList<ReviewComment> GetKeptComments()
    {
        return this.Decisions
            .Where(decision => decision.IsKeep)
            .Select(decision => decision.OriginalComment)
            .ToList()
            .AsReadOnly();
    }

    /// <summary>
    ///     Returns the stable recorded output shape used by diagnostics and comparison workflows.
    /// </summary>
    public RecordedFilterOutput ToRecordedOutput()
    {
        var discarded = this.Decisions
            .Where(decision => decision.IsDiscard)
            .Select(decision => new RecordedDiscardedFilterComment(
                decision.OriginalComment.FilePath ?? this.FilePath,
                decision.OriginalComment.LineNumber,
                decision.OriginalComment.Severity.ToString().ToLowerInvariant(),
                decision.OriginalComment.Message,
                decision.ReasonCodes,
                decision.DecisionSource))
            .ToList()
            .AsReadOnly();

        return new RecordedFilterOutput(
            this.ImplementationId,
            this.ImplementationVersion,
            this.FilePath,
            this.OriginalCommentCount,
            this.KeptCount,
            this.DiscardedCount,
            this.ReasonBuckets,
            BuildDecisionSources(this.Decisions),
            discarded,
            this.DegradedComponents,
            this.FallbackChecks,
            this.DegradedCause,
            this.AiTokenUsage);
    }

    private static IReadOnlyDictionary<string, int> BuildReasonBuckets(IEnumerable<CommentRelevanceFilterDecision> decisions)
    {
        var counts = decisions
            .Where(decision => decision.IsDiscard)
            .SelectMany(decision => decision.ReasonCodes)
            .GroupBy(code => code, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        return counts.Count == 0
            ? EmptyCounts
            : new ReadOnlyDictionary<string, int>(counts);
    }

    private static IReadOnlyDictionary<string, int> BuildDecisionSources(IEnumerable<CommentRelevanceFilterDecision> decisions)
    {
        var counts = decisions
            .GroupBy(decision => decision.DecisionSource, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        return counts.Count == 0
            ? EmptyCounts
            : new ReadOnlyDictionary<string, int>(counts);
    }
}
