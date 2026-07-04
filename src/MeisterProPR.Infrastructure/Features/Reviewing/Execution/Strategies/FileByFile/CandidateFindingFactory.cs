// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Features.Reviewing.Execution.Services;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

/// <summary>
///     Builds canonical candidate findings from completed per-file review results and from post-processing
///     comment sets such as deduped or quality-filtered comments. It preserves provenance for findings that
///     originated from a file review pass and creates derived findings only when a post-processing step
///     introduces comments that no longer map one-to-one to the original stored findings.
/// </summary>
internal sealed class CandidateFindingFactory(IReviewClaimExtractor? reviewClaimExtractor)
{
    public List<CandidateReviewFinding> Build(
        IReadOnlyList<ReviewFileResult> freshResults,
        IReadOnlyList<ReviewComment>? commentsOverride = null,
        ReviewPassKind passKind = ReviewPassKind.Baseline,
        IReadOnlyDictionary<string, IReadOnlyList<(int Start, int End)>>? changedLineRangesByPath = null)
    {
        var originalFindings = new List<CandidateReviewFinding>();
        var findingsBySignature = new Dictionary<string, Queue<CandidateReviewFinding>>(StringComparer.Ordinal);
        var provenanceKind = GetProvenanceKind(passKind);

        foreach (var fileResult in freshResults.Where(result => result.IsComplete && result.Comments is not null))
        {
            var comments = fileResult.Comments!;

            for (var index = 0; index < comments.Count; index++)
            {
                var comment = comments[index];
                var normalizedLineNumber = FileByFileReviewOrchestrator.NormalizeLineNumber(comment.LineNumber);
                var finding = new CandidateReviewFinding(
                    FileByFileReviewOrchestrator.BuildPerFileFindingId(fileResult, index + 1),
                    new CandidateFindingProvenance(
                        CandidateFindingProvenance.PerFileCommentOrigin,
                        "per_file_review",
                        fileResult.FilePath,
                        fileResult.Id,
                        index + 1,
                        reviewPassKind: ResolveCommentPassKind(comment, passKind),
                        findingProvenanceKind: provenanceKind,
                        unionArmLabel: ResolveUnionArmLabel(comment)),
                    comment.Severity,
                    comment.Message,
                    FileByFileReviewOrchestrator.DetermineCategory(comment),
                    comment.FilePath,
                    normalizedLineNumber,
                    invariantCheckContext: this.BuildInvariantCheckContext(fileResult, comment, index + 1),
                    scopeRelation: ClassifyScopeRelation(comment.FilePath ?? fileResult.FilePath, normalizedLineNumber, changedLineRangesByPath));
                originalFindings.Add(finding);

                var signature = CreateCommentSignature(comment);
                if (!findingsBySignature.TryGetValue(signature, out var queue))
                {
                    queue = new Queue<CandidateReviewFinding>();
                    findingsBySignature[signature] = queue;
                }

                queue.Enqueue(finding);
            }
        }

        if (commentsOverride is null)
        {
            return originalFindings;
        }

        var finalFindings = new List<CandidateReviewFinding>(commentsOverride.Count);
        var derivedOrdinal = 1;
        foreach (var comment in commentsOverride)
        {
            var signature = CreateCommentSignature(comment);
            if (findingsBySignature.TryGetValue(signature, out var queue) && queue.Count > 0)
            {
                finalFindings.Add(queue.Dequeue());
                continue;
            }

            finalFindings.Add(this.CreateDerivedCandidateFinding(comment, derivedOrdinal++, passKind, changedLineRangesByPath));
        }

        return finalFindings;
    }

    /// <summary>
    ///     Deterministically classifies a finding's anchor line against the file's changed-line ranges.
    ///     Returns <see langword="null" /> when the path is unknown, the line is unknown, or the file has no
    ///     resolvable changed ranges, so such findings are never labeled.
    /// </summary>
    private static ChangedLineRelation? ClassifyScopeRelation(
        string? filePath,
        int? lineNumber,
        IReadOnlyDictionary<string, IReadOnlyList<(int Start, int End)>>? changedLineRangesByPath)
    {
        if (filePath is null || changedLineRangesByPath is null ||
            !changedLineRangesByPath.TryGetValue(ReviewDiffProcessor.NormalizeReviewPath(filePath), out var ranges))
        {
            return null;
        }

        return ReviewDiffProcessor.ClassifyChangedLineRelation(lineNumber, ranges);
    }

    public static IReadOnlyList<CandidateReviewFinding> MergeFindings(
        IReadOnlyList<CandidateReviewFinding> baselineFindings,
        IReadOnlyList<CandidateReviewFinding> augmentationFindings)
    {
        if (augmentationFindings.Count == 0)
        {
            return baselineFindings
                .Select(finding => finding.WithMergedProvenance(
                    FindingProvenanceKind.BaselineOnly,
                    [ReviewPassKind.Baseline],
                    "baseline_only",
                    CreateFindingIdentityKey(finding)))
                .ToList();
        }

        var augmentationByIdentity = augmentationFindings
            .GroupBy(CreateFindingIdentityKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => new Queue<CandidateReviewFinding>(group), StringComparer.Ordinal);
        var merged = new List<CandidateReviewFinding>(baselineFindings.Count + augmentationFindings.Count);

        foreach (var baselineFinding in baselineFindings)
        {
            var identityKey = CreateFindingIdentityKey(baselineFinding);
            if (augmentationByIdentity.TryGetValue(identityKey, out var matches) && matches.Count > 0)
            {
                matches.Dequeue();
                merged.Add(
                    baselineFinding.WithMergedProvenance(
                        FindingProvenanceKind.Both,
                        [ReviewPassKind.Baseline, ReviewPassKind.ProRVAugmentation],
                        "exact_identity_match",
                        identityKey));
                continue;
            }

            merged.Add(
                baselineFinding.WithMergedProvenance(
                    FindingProvenanceKind.BaselineOnly,
                    [ReviewPassKind.Baseline],
                    "baseline_only",
                    identityKey));
        }

        foreach (var remaining in augmentationByIdentity)
        {
            while (remaining.Value.Count > 0)
            {
                var augmentationFinding = remaining.Value.Dequeue();
                merged.Add(
                    augmentationFinding.WithMergedProvenance(
                        FindingProvenanceKind.ProRVOnly,
                        [ReviewPassKind.ProRVAugmentation],
                        "augmentation_only",
                        remaining.Key));
            }
        }

        return merged;
    }

    public static IReadOnlyList<CandidateReviewFinding> AssignSynthesisFindingIds(IReadOnlyList<CandidateReviewFinding> synthesizedFindings)
    {
        if (synthesizedFindings.Count == 0)
        {
            return [];
        }

        var assigned = new List<CandidateReviewFinding>(synthesizedFindings.Count);
        for (var index = 0; index < synthesizedFindings.Count; index++)
        {
            var finding = synthesizedFindings[index];
            assigned.Add(
                new CandidateReviewFinding(
                    $"finding-cc-{index + 1:D3}",
                    finding.Provenance,
                    finding.Severity,
                    finding.Message,
                    finding.Category,
                    finding.FilePath,
                    finding.LineNumber,
                    finding.Evidence,
                    finding.CandidateSummaryText,
                    finding.InvariantCheckContext,
                    finding.VerificationOutcome,
                    finding.ScopeRelation));
        }

        return assigned;
    }

    private static string CreateCommentSignature(ReviewComment comment)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{comment.FilePath}|{FileByFileReviewOrchestrator.NormalizeLineNumber(comment.LineNumber)}|{comment.Severity}|{comment.Message}");
    }

    private CandidateReviewFinding CreateDerivedCandidateFinding(
        ReviewComment comment,
        int ordinal,
        ReviewPassKind passKind,
        IReadOnlyDictionary<string, IReadOnlyList<(int Start, int End)>>? changedLineRangesByPath)
    {
        var provenanceKind = GetProvenanceKind(passKind);
        if (TryBuildDerivedCrossFileEvidence(comment, out var evidence))
        {
            var findingId = $"finding-dedup-{ordinal:D3}";
            return new CandidateReviewFinding(
                findingId,
                new CandidateFindingProvenance(
                    CandidateFindingProvenance.PerFileCommentOrigin,
                    "finding_deduplication",
                    reviewPassKind: passKind,
                    findingProvenanceKind: provenanceKind),
                comment.Severity,
                comment.Message,
                CandidateReviewFinding.CrossCuttingCategory,
                null,
                null,
                evidence,
                "Cross-file concern derived from multiple per-file findings.",
                this.BuildInvariantCheckContext(findingId, comment, CandidateReviewFinding.CrossCuttingCategory, null, null, evidence));
        }

        var derivedFindingId = $"finding-derived-{ordinal:D3}";
        var normalizedLineNumber = FileByFileReviewOrchestrator.NormalizeLineNumber(comment.LineNumber);
        return new CandidateReviewFinding(
            derivedFindingId,
            new CandidateFindingProvenance(
                CandidateFindingProvenance.PerFileCommentOrigin,
                comment.FilePath is null ? "post_processing" : "quality_filter",
                comment.FilePath,
                reviewPassKind: passKind,
                findingProvenanceKind: provenanceKind),
            comment.Severity,
            comment.Message,
            FileByFileReviewOrchestrator.DetermineCategory(comment),
            comment.FilePath,
            normalizedLineNumber,
            invariantCheckContext: this.BuildInvariantCheckContext(
                derivedFindingId,
                comment,
                FileByFileReviewOrchestrator.DetermineCategory(comment),
                comment.FilePath,
                normalizedLineNumber,
                null),
            scopeRelation: ClassifyScopeRelation(comment.FilePath, normalizedLineNumber, changedLineRangesByPath));
    }

    private IReadOnlyDictionary<string, string>? BuildInvariantCheckContext(
        string findingId,
        ReviewComment comment,
        string category,
        string? filePath,
        int? lineNumber,
        EvidenceReference? evidence)
    {
        if (reviewClaimExtractor is null)
        {
            return null;
        }

        var normalizedLineNumber = FileByFileReviewOrchestrator.NormalizeLineNumber(lineNumber);
        var probeFinding = new CandidateReviewFinding(
            findingId,
            new CandidateFindingProvenance(
                CandidateFindingProvenance.PerFileCommentOrigin,
                "routing_probe",
                filePath,
                reviewPassKind: ReviewPassKind.Baseline,
                findingProvenanceKind: FindingProvenanceKind.BaselineOnly),
            comment.Severity,
            comment.Message,
            category,
            filePath,
            normalizedLineNumber,
            evidence);

        var claims = reviewClaimExtractor.ExtractClaims(probeFinding);
        return claims.Count == 0 ? null : CandidateReviewFinding.CreateInvariantCheckContext(claims);
    }

    private IReadOnlyDictionary<string, string>? BuildInvariantCheckContext(
        ReviewFileResult fileResult,
        ReviewComment comment,
        int ordinal)
    {
        if (reviewClaimExtractor is null)
        {
            return null;
        }

        var normalizedLineNumber = FileByFileReviewOrchestrator.NormalizeLineNumber(comment.LineNumber);
        var probeFinding = new CandidateReviewFinding(
            FileByFileReviewOrchestrator.BuildPerFileFindingId(fileResult, ordinal),
            new CandidateFindingProvenance(
                CandidateFindingProvenance.PerFileCommentOrigin,
                "per_file_review",
                fileResult.FilePath,
                fileResult.Id,
                ordinal,
                reviewPassKind: ReviewPassKind.Baseline,
                findingProvenanceKind: FindingProvenanceKind.BaselineOnly),
            comment.Severity,
            comment.Message,
            FileByFileReviewOrchestrator.DetermineCategory(comment),
            comment.FilePath,
            normalizedLineNumber);

        var claims = reviewClaimExtractor.ExtractClaims(probeFinding);
        return claims.Count == 0 ? null : CandidateReviewFinding.CreateInvariantCheckContext(claims);
    }

    private static FindingProvenanceKind GetProvenanceKind(ReviewPassKind passKind)
    {
        return passKind == ReviewPassKind.ProRVAugmentation
            ? FindingProvenanceKind.ProRVOnly
            : FindingProvenanceKind.BaselineOnly;
    }

    // A per-file comment tagged with the multi-pass union origin overrides the enclosing pass kind so the
    // finding's provenance records that it came from a union resample. Untagged comments keep the pass kind of
    // the enclosing build, so the single-pass path is unaffected.
    private static ReviewPassKind ResolveCommentPassKind(ReviewComment comment, ReviewPassKind passKind)
    {
        return IsMultiPassUnionOrigin(comment) ? ReviewPassKind.MultiPassUnion : passKind;
    }

    private static string? ResolveUnionArmLabel(ReviewComment comment)
    {
        return IsMultiPassUnionOrigin(comment) ? comment.OriginPassKind : null;
    }

    private static bool IsMultiPassUnionOrigin(ReviewComment comment)
    {
        return string.Equals(comment.OriginPassKind, nameof(ReviewPassKind.MultiPassUnion), StringComparison.Ordinal);
    }

    private static string CreateFindingIdentityKey(CandidateReviewFinding finding)
    {
        return $"{finding.FilePath}|{FileByFileReviewOrchestrator.NormalizeLineNumber(finding.LineNumber)}|{finding.Severity}|{finding.Message.Trim()}"
            .ToLowerInvariant();
    }

    private static bool TryBuildDerivedCrossFileEvidence(ReviewComment comment, out EvidenceReference? evidence)
    {
        evidence = null;
        if (comment.FilePath is not null || !comment.Message.StartsWith("[Cross-file]", StringComparison.Ordinal))
        {
            return false;
        }

        const string affectedFilesMarker = "Affected files:";
        var markerIndex = comment.Message.IndexOf(affectedFilesMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return false;
        }

        var filesText = comment.Message[(markerIndex + affectedFilesMarker.Length)..];
        var files = filesText
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length < 2)
        {
            return false;
        }

        evidence = new EvidenceReference([], files, EvidenceReference.ResolvedState, "derived_from_per_file_findings");
        return true;
    }
}
