// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
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
        IReadOnlyList<ReviewComment>? commentsOverride = null)
    {
        var originalFindings = new List<CandidateReviewFinding>();
        var findingsBySignature = new Dictionary<string, Queue<CandidateReviewFinding>>(StringComparer.Ordinal);

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
                        index + 1),
                    comment.Severity,
                    comment.Message,
                    FileByFileReviewOrchestrator.DetermineCategory(comment),
                    comment.FilePath,
                    normalizedLineNumber,
                    invariantCheckContext: this.BuildInvariantCheckContext(fileResult, comment, index + 1));
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

            finalFindings.Add(this.CreateDerivedCandidateFinding(comment, derivedOrdinal++));
        }

        return finalFindings;
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
            $"{comment.FilePath}|{FileByFileReviewOrchestrator.NormalizeLineNumber(comment.LineNumber)}|{comment.Severity}|{comment.Message}");
    }

    private CandidateReviewFinding CreateDerivedCandidateFinding(ReviewComment comment, int ordinal)
    {
        if (TryBuildDerivedCrossFileEvidence(comment, out var evidence))
        {
            var findingId = $"finding-dedup-{ordinal:D3}";
            return new CandidateReviewFinding(
                findingId,
                new CandidateFindingProvenance(
                    CandidateFindingProvenance.PerFileCommentOrigin,
                    "finding_deduplication"),
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
                comment.FilePath),
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

        var normalizedLineNumber = FileByFileReviewOrchestrator.NormalizeLineNumber(lineNumber);
        var probeFinding = new CandidateReviewFinding(
            findingId,
            new CandidateFindingProvenance(
                CandidateFindingProvenance.PerFileCommentOrigin,
                "routing_probe",
                filePath),
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
                ordinal),
            comment.Severity,
            comment.Message,
            FileByFileReviewOrchestrator.DetermineCategory(comment),
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
