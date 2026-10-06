// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;

internal sealed class LocalReviewVerificationExecutor(
    IReviewClaimExtractor? reviewClaimExtractor,
    IReviewFindingVerifier? reviewFindingVerifier,
    IProtocolRecorder protocolRecorder,
    ILogger<LocalReviewVerificationExecutor>? logger = null)
{
    private const int MaxRecordedReasonChars = 280;

    private const string JudgeUnavailableSummary =
        "Verification could not run for this file, so its findings were withheld. The verification_degraded event in the review protocol states the cause.";

    private static readonly JsonSerializerOptions FinalGateJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    public bool IsEnabled => reviewClaimExtractor is not null && reviewFindingVerifier is not null;

    /// <summary>
    ///     Verifies the findings of one file review. Every finding is marked so that its claim needs evidence, so each
    ///     finding that no invariant fact contradicts reaches the evidence-backed judge before it can be published.
    /// </summary>
    public async Task<ReviewResult> ApplyAsync(
        ReviewResult result,
        ReviewFileResult fileResult,
        Guid? protocolId,
        IReadOnlyList<InvariantFact> invariantFacts,
        ReviewVerificationContext? verificationContext,
        CancellationToken ct)
    {
        return (await this.ApplyDetailedAsync(result, fileResult, protocolId, invariantFacts, null, verificationContext, ct)).Result;
    }

    public async Task<LocalVerificationApplicationResult> ApplyDetailedAsync(
        ReviewResult result,
        ReviewFileResult fileResult,
        Guid? protocolId,
        IReadOnlyList<InvariantFact> invariantFacts,
        IReadOnlyList<CandidateReviewFinding>? enrichedCandidateFindings,
        ReviewVerificationContext? verificationContext,
        CancellationToken ct)
    {
        if (reviewClaimExtractor is null || reviewFindingVerifier is null || result.Comments.Count == 0)
        {
            return new LocalVerificationApplicationResult(result, []);
        }

        // The judge decides every finding by truth, intent and contradiction. A finding is never published on the
        // strength of its wording or its claim family alone.
        var commentFindings = BuildCandidateFindings(result, fileResult, enrichedCandidateFindings)
            .Select(pair => (pair.Comment, Finding: RequireEvidenceVerification(pair.Finding)))
            .ToList();
        var candidateFindings = commentFindings.Select(pair => pair.Finding).ToList();

        var claimsByFindingId = await this.ExtractClaimsByFindingAsync(candidateFindings, protocolId, ct);
        var workItems = candidateFindings
            .SelectMany(finding => claimsByFindingId[finding.FindingId]
                .Select(claim => new VerificationWorkItem(
                    claim,
                    finding.Provenance,
                    claim.Stage,
                    VerificationWorkItem.AnchorOnlyScope,
                    false)))
            .ToList();

        await this.RecordExtractedClaimsAsync(protocolId, candidateFindings, claimsByFindingId, ct);

        // A finding that needs evidence but yielded no claim, because extraction failed or produced none, has nothing
        // the verifiers could confirm. It is withheld, so a failed extraction never publishes it unverified.
        var unclaimedEvidenceOutcomes = candidateFindings
            .Where(finding => finding.Provenance.RequiresEvidenceVerification && claimsByFindingId[finding.FindingId].Count == 0)
            .Select(WithholdForMissingClaims)
            .ToList();

        if (workItems.Count == 0 && unclaimedEvidenceOutcomes.Count == 0)
        {
            return new LocalVerificationApplicationResult(result, candidateFindings);
        }

        var verifiedOutcomes = workItems.Count == 0
            ? []
            : await reviewFindingVerifier.VerifyAsync(workItems, invariantFacts, verificationContext, ct);
        IReadOnlyList<VerificationOutcome> outcomes = [.. verifiedOutcomes, .. unclaimedEvidenceOutcomes];
        var outcomesByFindingId = outcomes
            .GroupBy(outcome => outcome.FindingId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<VerificationOutcome>)group.ToList(),
                StringComparer.Ordinal);

        await this.RecordOutcomesAsync(protocolId, candidateFindings, outcomes, ct);
        var judgeUnavailableForFile = await this.RecordJudgeDegradationAsync(protocolId, fileResult.FilePath, workItems.Count, verifiedOutcomes, ct);

        var withheldFindingIds = outcomesByFindingId
            .Where(entry => !AreLocalOutcomesPublishable(entry.Value))
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (withheldFindingIds.Count == 0)
        {
            return new LocalVerificationApplicationResult(
                result,
                candidateFindings.Select(finding => AttachVerificationOutcome(finding, outcomesByFindingId)).ToList());
        }

        var verifiedFindings = candidateFindings
            .Where(finding => !withheldFindingIds.Contains(finding.FindingId))
            .Select(finding => AttachVerificationOutcome(finding, outcomesByFindingId))
            .ToList();

        // The published comments are the reviewer's own comment objects, so the read grounding, the producing pass,
        // the model and the symbol attribution they carry reach synthesis and the trace unchanged.
        var verifiedComments = commentFindings
            .Where(pair => !withheldFindingIds.Contains(pair.Finding.FindingId))
            .Select(pair => pair.Comment)
            .ToList();
        var verifiedSummary = RewriteLocalVerificationSummary(candidateFindings, verifiedFindings, outcomesByFindingId);
        if (judgeUnavailableForFile)
        {
            verifiedSummary = string.Concat(verifiedSummary, Environment.NewLine, Environment.NewLine, JudgeUnavailableSummary);
        }

        return new LocalVerificationApplicationResult(
            result with
            {
                Summary = verifiedSummary,
                Comments = verifiedComments,
            },
            candidateFindings.Select(finding => AttachVerificationOutcome(finding, outcomesByFindingId)).ToList());
    }

    // Pairs each review comment with its candidate finding. With enriched candidates, a comment without a matching
    // candidate is left out, as before.
    private static List<(ReviewComment Comment, CandidateReviewFinding Finding)> BuildCandidateFindings(
        ReviewResult result,
        ReviewFileResult fileResult,
        IReadOnlyList<CandidateReviewFinding>? enrichedCandidateFindings)
    {
        if (enrichedCandidateFindings is not { Count: > 0 })
        {
            return result.Comments
                .Select((comment, index) => (comment, new CandidateReviewFinding(
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
                    FileByFileReviewOrchestrator.NormalizeLineNumber(comment.LineNumber))))
                .ToList();
        }

        var enrichedBySignature = new Dictionary<string, Queue<CandidateReviewFinding>>(StringComparer.Ordinal);
        foreach (var finding in enrichedCandidateFindings)
        {
            var signature = CreateCommentSignature(finding);
            if (!enrichedBySignature.TryGetValue(signature, out var queue))
            {
                queue = new Queue<CandidateReviewFinding>();
                enrichedBySignature[signature] = queue;
            }

            queue.Enqueue(finding);
        }

        var candidateFindings = new List<(ReviewComment Comment, CandidateReviewFinding Finding)>(result.Comments.Count);
        foreach (var comment in result.Comments)
        {
            var signature = CreateCommentSignature(comment);
            if (enrichedBySignature.TryGetValue(signature, out var queue) && queue.Count > 0)
            {
                candidateFindings.Add((comment, ElevateProRvOnlyFinding(queue.Dequeue())));
            }
        }

        return candidateFindings;
    }

    private static CandidateReviewFinding ElevateProRvOnlyFinding(CandidateReviewFinding finding)
    {
        if (finding.Provenance.FindingProvenanceKind != FindingProvenanceKind.ProRVOnly ||
            finding.Provenance.RequiresExplicitSupport)
        {
            return finding;
        }

        return new CandidateReviewFinding(
            finding.FindingId,
            new CandidateFindingProvenance(
                finding.Provenance.OriginKind,
                finding.Provenance.GeneratedByStage,
                finding.Provenance.SourceFilePath,
                finding.Provenance.SourceFileResultId,
                finding.Provenance.SourceCommentOrdinal,
                finding.Provenance.EvidenceSetId,
                true,
                finding.Provenance.SourceOriginId,
                finding.Provenance.ReviewPassKind,
                finding.Provenance.FindingProvenanceKind),
            finding.Severity,
            finding.Message,
            finding.Category,
            finding.FilePath,
            finding.LineNumber,
            finding.Evidence,
            finding.CandidateSummaryText,
            finding.InvariantCheckContext,
            finding.VerificationOutcome)
        {
            MergedFinding = finding.MergedFinding,
        };
    }

    private static CandidateReviewFinding RequireEvidenceVerification(CandidateReviewFinding finding)
    {
        return finding with { Provenance = finding.Provenance with { RequiresEvidenceVerification = true } };
    }

    private static VerificationOutcome WithholdForMissingClaims(CandidateReviewFinding finding)
    {
        return new VerificationOutcome(
            $"{finding.FindingId}:claim:none",
            finding.FindingId,
            VerificationOutcome.NonVerifiableKind,
            FinalGateDecision.SummaryOnlyDisposition,
            [ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport],
            [],
            VerificationOutcome.NoEvidence,
            "The finding needs evidence verification, but no verifiable claim could be extracted from it.",
            VerificationOutcome.DeterministicRulesEvaluator,
            true);
    }

    private static CandidateReviewFinding AttachVerificationOutcome(
        CandidateReviewFinding finding,
        IReadOnlyDictionary<string, IReadOnlyList<VerificationOutcome>> outcomesByFindingId)
    {
        if (!outcomesByFindingId.TryGetValue(finding.FindingId, out var outcomes) || outcomes.Count == 0)
        {
            return finding;
        }

        return new CandidateReviewFinding(
            finding.FindingId,
            finding.Provenance,
            finding.Severity,
            finding.Message,
            finding.Category,
            finding.FilePath,
            finding.LineNumber,
            finding.Evidence,
            finding.CandidateSummaryText,
            finding.InvariantCheckContext,
            outcomes[0]);
    }

    private static string CreateCommentSignature(ReviewComment comment)
    {
        return string.Concat(
            comment.FilePath ?? string.Empty,
            "|",
            FileByFileReviewOrchestrator.NormalizeLineNumber(comment.LineNumber)?.ToString() ?? string.Empty,
            "|",
            comment.Severity,
            "|",
            comment.Message);
    }

    private static string CreateCommentSignature(CandidateReviewFinding finding)
    {
        return string.Concat(
            finding.FilePath ?? string.Empty,
            "|",
            FileByFileReviewOrchestrator.NormalizeLineNumber(finding.LineNumber)?.ToString() ?? string.Empty,
            "|",
            finding.Severity,
            "|",
            finding.Message);
    }

    private async Task<Dictionary<string, IReadOnlyList<ClaimDescriptor>>> ExtractClaimsByFindingAsync(
        IReadOnlyList<CandidateReviewFinding> candidateFindings,
        Guid? protocolId,
        CancellationToken ct)
    {
        var claimsByFindingId = new Dictionary<string, IReadOnlyList<ClaimDescriptor>>(StringComparer.Ordinal);
        foreach (var finding in candidateFindings)
        {
            try
            {
                claimsByFindingId[finding.FindingId] = reviewClaimExtractor!.ExtractClaims(finding);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                claimsByFindingId[finding.FindingId] = [];
                if (protocolId.HasValue)
                {
                    await protocolRecorder.RecordVerificationEventAsync(
                        protocolId.Value,
                        ReviewProtocolEventNames.VerificationDegraded,
                        JsonSerializer.Serialize(
                            new
                            {
                                findingId = finding.FindingId,
                                stage = ClaimDescriptor.LocalStage,
                                degradedComponent = "claim_extraction",
                            }),
                        null,
                        ex.Message,
                        ct);
                }
            }
        }

        return claimsByFindingId;
    }

    private async Task RecordExtractedClaimsAsync(
        Guid? protocolId,
        IReadOnlyList<CandidateReviewFinding> candidateFindings,
        IReadOnlyDictionary<string, IReadOnlyList<ClaimDescriptor>> claimsByFindingId,
        CancellationToken ct)
    {
        if (!protocolId.HasValue)
        {
            return;
        }

        foreach (var finding in candidateFindings)
        {
            var claims = claimsByFindingId[finding.FindingId];
            if (claims.Count == 0)
            {
                continue;
            }

            await protocolRecorder.RecordVerificationEventAsync(
                protocolId.Value,
                ReviewProtocolEventNames.VerificationClaimsExtracted,
                JsonSerializer.Serialize(
                    new
                    {
                        findingId = finding.FindingId,
                        filePath = finding.FilePath,
                        claimCount = claims.Count,
                    }),
                JsonSerializer.Serialize(claims, FinalGateJsonOptions),
                null,
                ct);
        }
    }

    // Records one decision per finding claim with the anchor, the judge verdict and the reason, so the trace shows why
    // each finding was published or withheld.
    private async Task RecordOutcomesAsync(
        Guid? protocolId,
        IReadOnlyList<CandidateReviewFinding> candidateFindings,
        IReadOnlyList<VerificationOutcome> outcomes,
        CancellationToken ct)
    {
        if (!protocolId.HasValue)
        {
            return;
        }

        var findingsById = candidateFindings
            .GroupBy(finding => finding.FindingId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var outcome in outcomes)
        {
            findingsById.TryGetValue(outcome.FindingId, out var finding);
            await protocolRecorder.RecordVerificationEventAsync(
                protocolId.Value,
                ReviewProtocolEventNames.VerificationLocalDecision,
                JsonSerializer.Serialize(
                    new
                    {
                        findingId = outcome.FindingId,
                        claimId = outcome.ClaimId,
                        filePath = finding?.FilePath,
                        lineNumber = finding?.LineNumber,
                        disposition = outcome.RecommendedDisposition,
                        judgeVerdict = outcome.JudgeVerdict,
                        evaluator = outcome.EvaluatedBy,
                        reason = TruncateReason(outcome.EvidenceSummary),
                    }),
                JsonSerializer.Serialize(outcome, FinalGateJsonOptions),
                null,
                ct);
        }
    }

    // Counts the claims the judge could not decide and, when there is at least one, records a verification_degraded
    // event for the file with the counts per reason and logs a warning. Returns true when the judge could decide none of
    // the file's claims, so the caller can state in the summary that verification did not run.
    private async Task<bool> RecordJudgeDegradationAsync(
        Guid? protocolId,
        string filePath,
        int judgedClaimCount,
        IReadOnlyList<VerificationOutcome> outcomes,
        CancellationToken ct)
    {
        var degraded = outcomes.Where(outcome => outcome.JudgeDegradation is not null).ToList();
        if (degraded.Count == 0)
        {
            return false;
        }

        var reasons = degraded
            .GroupBy(outcome => outcome.JudgeDegradation!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var allDegraded = degraded.Count == judgedClaimCount;
        this._logger.LogWarning(
            "The evidence judge could not decide {DegradedCount} of {ClaimCount} claims in {FilePath}; the affected findings were withheld. Reasons: {Reasons}",
            degraded.Count,
            judgedClaimCount,
            filePath,
            string.Join(", ", reasons.Select(reason => $"{reason.Key}={reason.Value}")));

        if (protocolId.HasValue)
        {
            var record = new
            {
                filePath,
                stage = ClaimDescriptor.LocalStage,
                degradedComponent = "evidence_judge",
                claimCount = judgedClaimCount,
                summaryOnlyCount = degraded.Count,
                degradedCount = degraded.Count,
                allClaimsDegraded = allDegraded,
                reasons,
            };
            await protocolRecorder.RecordVerificationEventAsync(
                protocolId.Value,
                ReviewProtocolEventNames.VerificationDegraded,
                JsonSerializer.Serialize(record),
                JsonSerializer.Serialize(record, FinalGateJsonOptions),
                $"The evidence judge could not decide {degraded.Count} of {judgedClaimCount} claims; the affected findings were withheld.",
                ct);
        }

        return allDegraded;
    }

    private static string? TruncateReason(string? reason)
    {
        return reason is null || reason.Length <= MaxRecordedReasonChars ? reason : reason[..MaxRecordedReasonChars];
    }

    private static bool AreLocalOutcomesPublishable(IReadOnlyList<VerificationOutcome> outcomes)
    {
        return outcomes.Count == 0 || outcomes.All(outcome =>
            string.Equals(outcome.RecommendedDisposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal));
    }

    private static string RewriteLocalVerificationSummary(
        IReadOnlyList<CandidateReviewFinding> originalFindings,
        IReadOnlyList<CandidateReviewFinding> verifiedFindings,
        IReadOnlyDictionary<string, IReadOnlyList<VerificationOutcome>> outcomesByFindingId)
    {
        var summaryOnlyCount = 0;
        var dropCount = 0;

        foreach (var finding in originalFindings)
        {
            if (!outcomesByFindingId.TryGetValue(finding.FindingId, out var outcomes) || AreLocalOutcomesPublishable(outcomes))
            {
                continue;
            }

            if (outcomes.Any(outcome => string.Equals(outcome.RecommendedDisposition, FinalGateDecision.DropDisposition, StringComparison.Ordinal)))
            {
                dropCount++;
                continue;
            }

            summaryOnlyCount++;
        }

        if (verifiedFindings.Count == 0)
        {
            var noFindingsBuilder = new StringBuilder("No actionable local findings remained after verification.");
            AppendLocalVerificationSuppressionSummary(noFindingsBuilder, summaryOnlyCount, dropCount);
            return noFindingsBuilder.ToString();
        }

        var builder = new StringBuilder();
        builder.Append($"Local verification retained {verifiedFindings.Count} actionable finding");
        builder.Append(verifiedFindings.Count == 1 ? "." : "s.");
        AppendLocalVerificationSuppressionSummary(builder, summaryOnlyCount, dropCount);

        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("Verified local findings:");

        foreach (var message in verifiedFindings
                     .Select(finding => finding.Message)
                     .Distinct(StringComparer.Ordinal)
                     .Take(5))
        {
            builder.Append("- ");
            builder.AppendLine(message);
        }

        return builder.ToString().TrimEnd();
    }

    private static void AppendLocalVerificationSuppressionSummary(StringBuilder builder, int summaryOnlyCount, int dropCount)
    {
        if (summaryOnlyCount > 0)
        {
            builder.Append(' ');
            builder.Append(summaryOnlyCount);
            builder.Append(
                summaryOnlyCount == 1
                    ? " candidate finding was withheld pending stronger evidence."
                    : " candidate findings were withheld pending stronger evidence.");
        }

        if (dropCount > 0)
        {
            builder.Append(' ');
            builder.Append(dropCount);
            builder.Append(
                dropCount == 1
                    ? " candidate finding was dropped by deterministic verification."
                    : " candidate findings were dropped by deterministic verification.");
        }
    }

    public sealed record LocalVerificationApplicationResult(ReviewResult Result, IReadOnlyList<CandidateReviewFinding> VerifiedCandidateFindings);
}
