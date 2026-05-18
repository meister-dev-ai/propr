// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using System.Text.Json;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;

internal sealed class LocalReviewVerificationExecutor(
    IReviewClaimExtractor? reviewClaimExtractor,
    IReviewFindingVerifier? reviewFindingVerifier,
    IProtocolRecorder protocolRecorder)
{
    private static readonly JsonSerializerOptions FinalGateJsonOptions = new(JsonSerializerDefaults.Web);

    public bool IsEnabled => reviewClaimExtractor is not null && reviewFindingVerifier is not null;

    public async Task<ReviewResult> ApplyAsync(
        ReviewResult result,
        ReviewFileResult fileResult,
        Guid? protocolId,
        IReadOnlyList<InvariantFact> invariantFacts,
        CancellationToken ct)
    {
        return (await this.ApplyDetailedAsync(result, fileResult, protocolId, invariantFacts, null, ct)).Result;
    }

    public async Task<LocalVerificationApplicationResult> ApplyDetailedAsync(
        ReviewResult result,
        ReviewFileResult fileResult,
        Guid? protocolId,
        IReadOnlyList<InvariantFact> invariantFacts,
        IReadOnlyList<CandidateReviewFinding>? enrichedCandidateFindings,
        CancellationToken ct)
    {
        if (reviewClaimExtractor is null || reviewFindingVerifier is null || result.Comments.Count == 0)
        {
            return new LocalVerificationApplicationResult(result, []);
        }

        var candidateFindings = BuildCandidateFindings(result, fileResult, enrichedCandidateFindings);

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

        if (workItems.Count == 0)
        {
            return new LocalVerificationApplicationResult(result, candidateFindings);
        }

        var outcomes = await reviewFindingVerifier.VerifyAsync(workItems, invariantFacts, ct);
        var outcomesByFindingId = outcomes
            .GroupBy(outcome => outcome.FindingId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<VerificationOutcome>)group.ToList(),
                StringComparer.Ordinal);

        await this.RecordOutcomesAsync(protocolId, outcomes, ct);

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
            .Where(finding => !outcomesByFindingId.TryGetValue(finding.FindingId, out var findingOutcomes) || AreLocalOutcomesPublishable(findingOutcomes))
            .Select(finding => AttachVerificationOutcome(finding, outcomesByFindingId))
            .ToList();
        var verifiedComments = verifiedFindings
            .Select(finding => FileByFileReviewOrchestrator.CreateReviewComment(finding.FilePath, finding.LineNumber, finding.Severity, finding.Message))
            .ToList();
        var verifiedSummary = RewriteLocalVerificationSummary(candidateFindings, verifiedFindings, outcomesByFindingId);

        return new LocalVerificationApplicationResult(
            result with
            {
                Summary = verifiedSummary,
                Comments = verifiedComments,
            },
            candidateFindings.Select(finding => AttachVerificationOutcome(finding, outcomesByFindingId)).ToList());
    }

    private static List<CandidateReviewFinding> BuildCandidateFindings(
        ReviewResult result,
        ReviewFileResult fileResult,
        IReadOnlyList<CandidateReviewFinding>? enrichedCandidateFindings)
    {
        if (enrichedCandidateFindings is not { Count: > 0 })
        {
            return result.Comments
                .Select((comment, index) => new CandidateReviewFinding(
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
                    FileByFileReviewOrchestrator.NormalizeLineNumber(comment.LineNumber)))
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

        var candidateFindings = new List<CandidateReviewFinding>(result.Comments.Count);
        foreach (var comment in result.Comments)
        {
            var signature = CreateCommentSignature(comment);
            if (enrichedBySignature.TryGetValue(signature, out var queue) && queue.Count > 0)
            {
                candidateFindings.Add(ElevateProRvOnlyFinding(queue.Dequeue()));
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

    private async Task RecordOutcomesAsync(
        Guid? protocolId,
        IReadOnlyList<VerificationOutcome> outcomes,
        CancellationToken ct)
    {
        if (!protocolId.HasValue)
        {
            return;
        }

        foreach (var outcome in outcomes)
        {
            await protocolRecorder.RecordVerificationEventAsync(
                protocolId.Value,
                ReviewProtocolEventNames.VerificationLocalDecision,
                JsonSerializer.Serialize(
                    new
                    {
                        findingId = outcome.FindingId,
                        claimId = outcome.ClaimId,
                    }),
                JsonSerializer.Serialize(outcome, FinalGateJsonOptions),
                null,
                ct);
        }
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
