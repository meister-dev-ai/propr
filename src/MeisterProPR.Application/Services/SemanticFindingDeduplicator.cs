// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Services;

/// <summary>
///     Provenance-aware semantic deduplicator. Two findings merge only when ALL of the following hold:
///     (1) same file, (2) overlapping anchor (line numbers within a small tolerance), and (3) the pluggable
///     <see cref="IFindingMergeJudge" /> confirms they describe the same defect class. (1) and (2) are
///     deterministic pre-filters evaluated before the judge is ever consulted, so distinct bugs in different
///     files or at non-overlapping anchors can never collapse — and two distinct bugs with overlapping
///     vocabulary in the same file stay separate whenever the judge says they are different defects. This closes
///     the token-Jaccard true-positive-loss class by construction: the merge is pairwise and gated on the
///     deterministic pre-filter, never on message-text similarity alone.
/// </summary>
public sealed class SemanticFindingDeduplicator : IFindingDeduplicator
{
    /// <summary>
    ///     Maximum line distance between two single-line anchors for their ranges to be treated as overlapping.
    ///     Deliberately small so only findings pointing at effectively the same location are merge candidates.
    /// </summary>
    public const int DefaultAnchorOverlapTolerance = 5;

    private readonly int _anchorOverlapTolerance;

    private readonly IFindingMergeJudge _mergeJudge;

    /// <summary>Initializes a new <see cref="SemanticFindingDeduplicator" />.</summary>
    /// <param name="mergeJudge">Judge that decides same-defect-class for anchor-overlapping same-file pairs.</param>
    /// <param name="anchorOverlapTolerance">Maximum line distance treated as an overlapping anchor.</param>
    public SemanticFindingDeduplicator(IFindingMergeJudge mergeJudge, int anchorOverlapTolerance = DefaultAnchorOverlapTolerance)
    {
        this._mergeJudge = mergeJudge ?? throw new ArgumentNullException(nameof(mergeJudge));
        this._anchorOverlapTolerance = anchorOverlapTolerance;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReviewComment>> DeduplicateAsync(
        IReadOnlyList<ReviewComment> comments,
        Guid clientId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(comments);
        if (comments.Count <= 1)
        {
            return comments;
        }

        // Re-stamp provenance onto each comment so the merge operates on provenance-aware findings rather than
        // bare, provenance-blind comments. The reconstructed comment for each survivor is keyed back by finding id.
        var commentsByFindingId = new Dictionary<string, ReviewComment>(StringComparer.Ordinal);
        var findings = new List<CandidateReviewFinding>(comments.Count);
        for (var index = 0; index < comments.Count; index++)
        {
            var finding = ToProvenanceAwareFinding(comments[index], index + 1);
            findings.Add(finding);
            commentsByFindingId[finding.FindingId] = comments[index];
        }

        var mergedFindings = await this.DeduplicateFindingsAsync(findings, clientId, ct).ConfigureAwait(false);

        var survivorComments = mergedFindings
            .Select(finding => commentsByFindingId[finding.FindingId])
            .ToList();

        // Cross-file root-cause consolidation is orthogonal to same-file semantic merging; keep applying it so
        // findings that recur across files still collapse into one PR-level comment.
        return FindingDeduplicator.Deduplicate(survivorComments);
    }

    /// <summary>
    ///     Collapses same-file, overlapping-anchor, same-defect-class findings into one, keeping the
    ///     higher-severity / better-anchored representative and unioning source-pass provenance. Input order is
    ///     preserved for the surviving representatives.
    /// </summary>
    /// <param name="findings">Provenance-aware candidate findings to merge.</param>
    /// <param name="clientId">Client whose model binding governs the same-defect-class judgment.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<CandidateReviewFinding>> DeduplicateFindingsAsync(
        IReadOnlyList<CandidateReviewFinding> findings,
        Guid clientId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(findings);
        if (findings.Count <= 1)
        {
            return findings;
        }

        var groups = new List<MergeGroup>();
        foreach (var finding in findings)
        {
            ct.ThrowIfCancellationRequested();

            MergeGroup? target = null;
            foreach (var group in groups)
            {
                if (await this.CanMergeAsync(group.Representative, finding, clientId, ct).ConfigureAwait(false))
                {
                    target = group;
                    break;
                }
            }

            if (target is null)
            {
                groups.Add(new MergeGroup(finding));
            }
            else
            {
                target.Add(finding, ChooseRepresentative(target.Representative, finding));
            }
        }

        var result = new List<CandidateReviewFinding>(groups.Count);
        foreach (var group in groups)
        {
            result.Add(group.MemberCount > 1 ? group.BuildMergedRepresentative() : group.Representative);
        }

        return result;
    }

    /// <summary>
    ///     The conservative pre-filter plus judgment gate for one pair. Returns <see langword="true" /> only when
    ///     the two findings share a file, have overlapping anchors, and the judge confirms the same defect class.
    ///     The judge is consulted only after the deterministic pre-filters pass.
    /// </summary>
    private async Task<bool> CanMergeAsync(
        CandidateReviewFinding first,
        CandidateReviewFinding second,
        Guid clientId,
        CancellationToken ct)
    {
        if (!IsSameFile(first, second) || !this.IsOverlappingAnchor(first, second))
        {
            return false;
        }

        return await this._mergeJudge.AreSameDefectClassAsync(first, second, clientId, ct).ConfigureAwait(false);
    }

    private static bool IsSameFile(CandidateReviewFinding first, CandidateReviewFinding second)
    {
        return first.FilePath is not null
               && second.FilePath is not null
               && string.Equals(first.FilePath, second.FilePath, StringComparison.Ordinal);
    }

    private bool IsOverlappingAnchor(CandidateReviewFinding first, CandidateReviewFinding second)
    {
        // A finding with no line number cannot be confirmed to overlap another, so it is never a merge candidate.
        if (first.LineNumber is not int a || second.LineNumber is not int b)
        {
            return false;
        }

        return Math.Abs(a - b) <= this._anchorOverlapTolerance;
    }

    // Prefers the higher-severity finding; ties break toward the better-anchored one (a known line number, then
    // the lower line), then the longer message, then a stable identity-key comparison for determinism.
    private static bool ShouldReplace(CandidateReviewFinding current, CandidateReviewFinding candidate)
    {
        var currentRank = SeverityRank(current.Severity);
        var candidateRank = SeverityRank(candidate.Severity);
        if (candidateRank != currentRank)
        {
            return candidateRank > currentRank;
        }

        var currentAnchored = current.LineNumber.HasValue;
        var candidateAnchored = candidate.LineNumber.HasValue;
        if (candidateAnchored != currentAnchored)
        {
            return candidateAnchored;
        }

        if (currentAnchored && candidate.LineNumber!.Value != current.LineNumber!.Value)
        {
            return candidate.LineNumber.Value < current.LineNumber.Value;
        }

        if (candidate.Message.Length != current.Message.Length)
        {
            return candidate.Message.Length > current.Message.Length;
        }

        return string.CompareOrdinal(candidate.FindingId, current.FindingId) < 0;
    }

    private static CandidateReviewFinding ChooseRepresentative(CandidateReviewFinding current, CandidateReviewFinding candidate)
    {
        return ShouldReplace(current, candidate) ? candidate : current;
    }

    private static int SeverityRank(CommentSeverity severity)
    {
        return severity switch
        {
            CommentSeverity.Error => 3,
            CommentSeverity.Warning => 2,
            CommentSeverity.Suggestion => 1,
            _ => 0,
        };
    }

    private static CandidateReviewFinding ToProvenanceAwareFinding(ReviewComment comment, int ordinal)
    {
        var isUnion = string.Equals(comment.OriginPassKind, nameof(ReviewPassKind.MultiPassUnion), StringComparison.Ordinal);
        var passKind = isUnion ? ReviewPassKind.MultiPassUnion : ReviewPassKind.Baseline;
        var findingId = string.Create(CultureInfo.InvariantCulture, $"finding-union-{ordinal:D4}");
        return new CandidateReviewFinding(
            findingId,
            new CandidateFindingProvenance(
                CandidateFindingProvenance.PerFileCommentOrigin,
                "multi_pass_union_dedup",
                comment.FilePath,
                reviewPassKind: passKind,
                findingProvenanceKind: FindingProvenanceKind.BaselineOnly,
                unionArmLabel: isUnion ? comment.OriginPassKind : null),
            comment.Severity,
            comment.Message,
            comment.FilePath is null ? CandidateReviewFinding.CrossCuttingCategory : CandidateReviewFinding.PerFileCommentCategory,
            comment.FilePath,
            comment.LineNumber);
    }

    // A mutable accumulator for one merge group: its current representative and the distinct passes that
    // contributed, so the union of source passes can be stamped onto the merged representative.
    private sealed class MergeGroup
    {
        private readonly HashSet<ReviewPassKind> _sourcePasses = [];

        public MergeGroup(CandidateReviewFinding representative)
        {
            this.Representative = representative;
            this._sourcePasses.Add(representative.Provenance.ReviewPassKind);
            this.MemberCount = 1;
        }

        public CandidateReviewFinding Representative { get; private set; }

        public int MemberCount { get; private set; }

        public void Add(CandidateReviewFinding member, CandidateReviewFinding chosenRepresentative)
        {
            this._sourcePasses.Add(member.Provenance.ReviewPassKind);
            this.Representative = chosenRepresentative;
            this.MemberCount++;
        }

        public CandidateReviewFinding BuildMergedRepresentative()
        {
            var sourcePasses = this._sourcePasses.OrderBy(p => p).ToList();
            var provenanceKind = ResolveProvenanceKind(sourcePasses);
            return this.Representative.WithMergedProvenance(
                provenanceKind,
                sourcePasses,
                "semantic_same_defect_class",
                this.Representative.FindingId);
        }

        private static FindingProvenanceKind ResolveProvenanceKind(IReadOnlyList<ReviewPassKind> sourcePasses)
        {
            var hasBaseline = sourcePasses.Any(p => p is ReviewPassKind.Baseline or ReviewPassKind.MultiPassUnion);
            var hasProRv = sourcePasses.Contains(ReviewPassKind.ProRVAugmentation);
            if (hasBaseline && hasProRv)
            {
                return FindingProvenanceKind.Both;
            }

            return hasProRv ? FindingProvenanceKind.ProRVOnly : FindingProvenanceKind.BaselineOnly;
        }
    }
}
