// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

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
                        unionPassIndex: ResolveUnionPassIndex(comment),
                        unionArmLabel: ResolveUnionArmLabel(comment),
                        unionLens: ResolveUnionLens(comment),
                        originModelId: comment.OriginModelId,
                        originLogicalModelName: comment.OriginLogicalModelName,
                        originSymbolName: comment.OriginSymbolName,
                        originSymbolKind: comment.OriginSymbolKind),
                    comment.Severity,
                    comment.Message,
                    FileByFileReviewOrchestrator.DetermineCategory(comment),
                    comment.FilePath,
                    normalizedLineNumber,
                    invariantCheckContext: this.BuildInvariantCheckContext(fileResult, comment, index + 1),
                    scopeRelation: ClassifyScopeRelation(comment.FilePath ?? fileResult.FilePath, normalizedLineNumber, changedLineRangesByPath))
                {
                    RiskAssessment = comment.RiskAssessment, FindingClassification = comment.FindingClassification, SuggestedFix = comment.SuggestedFix,
                    ReadGrounding = comment.SourceReadGrounding,
                    ProducedWithRepositoryTools = comment.ProducedWithRepositoryTools,
                };
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
                finalFindings.Add(
                    queue.Dequeue() with
                    {
                        RiskAssessment = comment.RiskAssessment, FindingClassification = comment.FindingClassification, SuggestedFix = comment.SuggestedFix,
                        ProducedWithRepositoryTools = comment.ProducedWithRepositoryTools,
                    });
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
    internal static ChangedLineRelation? ClassifyScopeRelation(
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
        // Per-file findings all originate from the baseline pass. The former late-augmentation second look is gone,
        // so there is nothing to merge; multi-pass-union resample/lens findings carry their own origin on the comment
        // (OriginPassKind/OriginPassLens) rather than through this per-file merge.
        _ = augmentationFindings;
        return baselineFindings
            .Select(finding => finding.WithMergedProvenance(
                FindingProvenanceKind.BaselineOnly,
                [ReviewPassKind.Baseline],
                "baseline_only",
                CreateFindingIdentityKey(finding)))
            .ToList();
    }

    internal static IReadOnlyList<CandidateReviewFinding> RetainSourceClassifications(
        IReadOnlyList<CandidateReviewFinding> findings, IReadOnlyList<CandidateReviewFinding> sources)
    {
        return findings.Select(finding =>
        {
            var unchanged = sources.Where(source => source.Message == finding.Message && source.FilePath == finding.FilePath
                                                                                      && source.LineNumber == finding.LineNumber).Take(2).ToList();
            return finding with
            {
                FindingClassification = FindingTypeClassificationPreserver.Preserve(
                    finding.FindingClassification, unchanged.Count == 1 ? unchanged[0].FindingClassification : null,
                    sources.Where(source => finding.Evidence?.SupportingFindingIds?.Contains(source.FindingId, StringComparer.Ordinal) == true)
                        .Select(source => source.FindingClassification)),
            };
        }).ToList();
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
            assigned.Add(finding.WithFindingId($"finding-cc-{index + 1:D3}"));
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
                        findingProvenanceKind: provenanceKind,
                        originModelId: comment.OriginModelId,
                        originLogicalModelName: comment.OriginLogicalModelName,
                        originSymbolName: comment.OriginSymbolName,
                        originSymbolKind: comment.OriginSymbolKind),
                    comment.Severity,
                    comment.Message,
                    CandidateReviewFinding.CrossCuttingCategory,
                    null,
                    null,
                    evidence,
                    null,
                    this.BuildInvariantCheckContext(findingId, comment, CandidateReviewFinding.CrossCuttingCategory, null, null, evidence))
                { RiskAssessment = comment.RiskAssessment, FindingClassification = comment.FindingClassification, SuggestedFix = comment.SuggestedFix };
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
                findingProvenanceKind: provenanceKind,
                originModelId: comment.OriginModelId,
                originLogicalModelName: comment.OriginLogicalModelName,
                originSymbolName: comment.OriginSymbolName,
                originSymbolKind: comment.OriginSymbolKind),
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
            scopeRelation: ClassifyScopeRelation(comment.FilePath, normalizedLineNumber, changedLineRangesByPath))
        {
            RiskAssessment = comment.RiskAssessment, FindingClassification = comment.FindingClassification, SuggestedFix = comment.SuggestedFix,
            ReadGrounding = comment.SourceReadGrounding,
            ProducedWithRepositoryTools = comment.ProducedWithRepositoryTools,
        };
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
            evidence) { RiskAssessment = comment.RiskAssessment, FindingClassification = comment.FindingClassification, SuggestedFix = comment.SuggestedFix };

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
                normalizedLineNumber)
            { RiskAssessment = comment.RiskAssessment, FindingClassification = comment.FindingClassification, SuggestedFix = comment.SuggestedFix };

        var claims = reviewClaimExtractor.ExtractClaims(probeFinding);
        return claims.Count == 0 ? null : CandidateReviewFinding.CreateInvariantCheckContext(claims);
    }

    private static FindingProvenanceKind GetProvenanceKind(ReviewPassKind passKind)
    {
        // Only the baseline and multi-pass-union passes remain; both classify as baseline-only for the per-file merge
        // provenance (a union finding surfaces its pass via ReviewPassKind/OriginPassIndex, not this classification).
        _ = passKind;
        return FindingProvenanceKind.BaselineOnly;
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

    // Carries the 1-based union pass index from a union-origin comment onto the finding's provenance so a
    // published finding can resolve its producing pass to "Pass N" rather than the baseline. Null for
    // non-union comments, which leaves the single-pass path unaffected.
    private static int? ResolveUnionPassIndex(ReviewComment comment)
    {
        return IsMultiPassUnionOrigin(comment) ? comment.OriginPassIndex : null;
    }

    // Carries the specialist lens (e.g. security) from a union-origin comment onto the finding's provenance so a
    // published finding can render "Pass N · <lens>". Null for non-union and ordinary resample comments.
    private static string? ResolveUnionLens(ReviewComment comment)
    {
        return IsMultiPassUnionOrigin(comment) ? comment.OriginPassLens : null;
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
