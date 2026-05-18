// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.AgenticFileByFile;

internal sealed class AgenticCandidateFindingFactory(IReviewClaimExtractor? reviewClaimExtractor)
{
    public List<CandidateReviewFinding> Build(
        IReadOnlyList<ReviewFileResult> freshResults,
        IReadOnlyList<ReviewComment>? commentsOverride = null,
        IReadOnlyList<CandidateReviewFinding>? enrichedPerFileFindings = null,
        ReviewPassKind passKind = ReviewPassKind.Baseline)
    {
        var originalFindings = new List<CandidateReviewFinding>();
        var provenanceKind = GetProvenanceKind(passKind);

        foreach (var fileResult in freshResults.Where(result => result.IsComplete && result.Comments is not null))
        {
            var comments = fileResult.Comments!;

            for (var index = 0; index < comments.Count; index++)
            {
                var comment = comments[index];
                var normalizedLineNumber = AgenticFileByFileReviewOrchestrator.NormalizeLineNumber(comment.LineNumber);
                var finding = new CandidateReviewFinding(
                    AgenticFileByFileReviewOrchestrator.BuildPerFileFindingId(fileResult, index + 1),
                    new CandidateFindingProvenance(
                        CandidateFindingProvenance.PerFileCommentOrigin,
                        "per_file_review",
                        fileResult.FilePath,
                        fileResult.Id,
                        index + 1,
                        reviewPassKind: passKind,
                        findingProvenanceKind: provenanceKind),
                    comment.Severity,
                    comment.Message,
                    AgenticFileByFileReviewOrchestrator.DetermineCategory(comment),
                    comment.FilePath,
                    normalizedLineNumber,
                    invariantCheckContext: this.BuildInvariantCheckContext(fileResult, comment, index + 1));
                originalFindings.Add(finding);
            }
        }

        var resolvedOriginalFindings = ReplaceWithEnrichedPerFileFindings(originalFindings, enrichedPerFileFindings);

        if (commentsOverride is null)
        {
            return resolvedOriginalFindings;
        }

        var findingsBySignature = new Dictionary<string, Queue<CandidateReviewFinding>>(StringComparer.Ordinal);
        foreach (var finding in resolvedOriginalFindings)
        {
            var signature = CreateCommentSignature(finding);
            if (!findingsBySignature.TryGetValue(signature, out var queue))
            {
                queue = new Queue<CandidateReviewFinding>();
                findingsBySignature[signature] = queue;
            }

            queue.Enqueue(finding);
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

            finalFindings.Add(this.CreateDerivedCandidateFinding(comment, derivedOrdinal++, passKind));
        }

        if (commentsOverride.Count == 0 && enrichedPerFileFindings is { Count: > 0 })
        {
            return resolvedOriginalFindings;
        }

        return finalFindings;
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
                    finding.VerificationOutcome));
        }

        return assigned;
    }

    private static string CreateCommentSignature(ReviewComment comment)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{comment.FilePath}|{AgenticFileByFileReviewOrchestrator.NormalizeLineNumber(comment.LineNumber)}|{comment.Severity}|{comment.Message}");
    }

    private static string CreateCommentSignature(CandidateReviewFinding finding)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{finding.FilePath}|{AgenticFileByFileReviewOrchestrator.NormalizeLineNumber(finding.LineNumber)}|{finding.Severity}|{finding.Message}");
    }

    private static List<CandidateReviewFinding> ReplaceWithEnrichedPerFileFindings(
        IReadOnlyList<CandidateReviewFinding> originalFindings,
        IReadOnlyList<CandidateReviewFinding>? enrichedPerFileFindings)
    {
        if (enrichedPerFileFindings is not { Count: > 0 })
        {
            return originalFindings.ToList();
        }

        var enrichedBySignature = new Dictionary<string, Queue<CandidateReviewFinding>>(StringComparer.Ordinal);
        foreach (var finding in enrichedPerFileFindings)
        {
            var signature = CreateCommentSignature(finding);
            if (!enrichedBySignature.TryGetValue(signature, out var queue))
            {
                queue = new Queue<CandidateReviewFinding>();
                enrichedBySignature[signature] = queue;
            }

            queue.Enqueue(finding);
        }

        var resolved = new List<CandidateReviewFinding>(Math.Max(originalFindings.Count, enrichedPerFileFindings.Count));
        foreach (var finding in originalFindings)
        {
            var signature = CreateCommentSignature(finding);
            if (enrichedBySignature.TryGetValue(signature, out var queue) && queue.Count > 0)
            {
                resolved.Add(queue.Dequeue());
                continue;
            }

            resolved.Add(finding);
        }

        foreach (var remaining in enrichedBySignature.Values)
        {
            while (remaining.Count > 0)
            {
                resolved.Add(remaining.Dequeue());
            }
        }

        return resolved;
    }

    private CandidateReviewFinding CreateDerivedCandidateFinding(ReviewComment comment, int ordinal, ReviewPassKind passKind)
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
        var normalizedLineNumber = AgenticFileByFileReviewOrchestrator.NormalizeLineNumber(comment.LineNumber);
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
            AgenticFileByFileReviewOrchestrator.DetermineCategory(comment),
            comment.FilePath,
            normalizedLineNumber,
            invariantCheckContext: this.BuildInvariantCheckContext(
                derivedFindingId,
                comment,
                AgenticFileByFileReviewOrchestrator.DetermineCategory(comment),
                comment.FilePath,
                normalizedLineNumber,
                null));
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

        var normalizedLineNumber = AgenticFileByFileReviewOrchestrator.NormalizeLineNumber(lineNumber);
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

    private static FindingProvenanceKind GetProvenanceKind(ReviewPassKind passKind)
    {
        return passKind == ReviewPassKind.ProRVAugmentation
            ? FindingProvenanceKind.ProRVOnly
            : FindingProvenanceKind.BaselineOnly;
    }

    private static string CreateFindingIdentityKey(CandidateReviewFinding finding)
    {
        return $"{finding.FilePath}|{AgenticFileByFileReviewOrchestrator.NormalizeLineNumber(finding.LineNumber)}|{finding.Severity}|{finding.Message.Trim()}"
            .ToLowerInvariant();
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

        var normalizedLineNumber = AgenticFileByFileReviewOrchestrator.NormalizeLineNumber(comment.LineNumber);
        var probeFinding = new CandidateReviewFinding(
            AgenticFileByFileReviewOrchestrator.BuildPerFileFindingId(fileResult, ordinal),
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
            AgenticFileByFileReviewOrchestrator.DetermineCategory(comment),
            comment.FilePath,
            normalizedLineNumber);

        var claims = reviewClaimExtractor.ExtractClaims(probeFinding);
        return claims.Count == 0 ? null : CandidateReviewFinding.CreateInvariantCheckContext(claims);
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
