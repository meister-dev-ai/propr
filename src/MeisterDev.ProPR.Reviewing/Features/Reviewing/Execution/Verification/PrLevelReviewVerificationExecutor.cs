// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.AI;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;

/// <summary>
///     Verifies synthesized PR-level findings and PR-wide pass findings before they enter the deterministic final gate.
///     For each finding it extracts the strongest claim, collects repository evidence for the trace and the gate, and
///     lets the evidence judge decide the claim with the same rule, verdicts, low-effort model, bounds and trace as a
///     file finding. A finding without a claim, or without a judge, is withheld.
/// </summary>
internal sealed class PrLevelReviewVerificationExecutor(
    IReviewClaimExtractor? reviewClaimExtractor,
    IReviewEvidenceCollector? reviewEvidenceCollector,
    IProtocolRecorder protocolRecorder,
    AiReviewOptions options,
    IReviewFindingVerifier? reviewFindingVerifier = null,
    IEnumerable<IReviewInvariantFactProvider>? reviewInvariantFactProviders = null)
{
    private static readonly JsonSerializerOptions FinalGateJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IReadOnlyList<InvariantFact> invariantFacts =
        reviewInvariantFactProviders?.SelectMany(provider => provider.GetFacts()).ToList() ?? [];

    /// <summary>
    ///     Applies bounded PR-level verification to synthesized cross-file findings.
    /// </summary>
    public async Task<IReadOnlyList<CandidateReviewFinding>> ApplyAsync(
        IReadOnlyList<CandidateReviewFinding> synthesizedFindings,
        ReviewSystemContext reviewContext,
        string sourceBranch,
        Guid? protocolId,
        IChatClient? fallbackChatClient,
        CancellationToken ct,
        Guid clientId = default,
        ReviewVerificationIntent? intent = null,
        IAiRuntimeResolver? aiRuntimeResolver = null)
    {
        if (synthesizedFindings.Count == 0 || reviewClaimExtractor is null || reviewEvidenceCollector is null)
        {
            return synthesizedFindings;
        }

        var verificationContext = new ReviewVerificationContext(
            reviewContext.ReviewTools,
            sourceBranch,
            reviewContext.DefaultReviewChatClient ?? reviewContext.TierChatClient ?? fallbackChatClient,
            reviewContext.DefaultReviewModelId ?? reviewContext.ModelId ?? options.ModelId,
            clientId,
            aiRuntimeResolver,
            intent,
            protocolId);

        // Each finding keeps its position in the result. A finding that the judge decides is collected first and all
        // of them are judged in one verifier call, so the judge runtime is resolved and the source reads are cached
        // once for the whole set.
        var verified = new CandidateReviewFinding?[synthesizedFindings.Count];
        var pendingJudgements = new List<PendingJudgement>();

        for (var index = 0; index < synthesizedFindings.Count; index++)
        {
            var effectiveFinding = ElevateProRvOnlyFinding(synthesizedFindings[index]);

            IReadOnlyList<ClaimDescriptor> claims;
            try
            {
                claims = reviewClaimExtractor.ExtractClaims(effectiveFinding);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                await this.RecordDegradedAsync(
                    protocolId,
                    new
                    {
                        findingId = effectiveFinding.FindingId,
                        stage = ClaimDescriptor.PrLevelStage,
                        degradedComponent = "claim_extraction",
                    },
                    null,
                    ex.Message,
                    ct);

                verified[index] = CreateClaimExtractionDegradedFinding(effectiveFinding, ex.Message);
                continue;
            }

            if (claims.Count == 0)
            {
                // Nothing the judge could decide, so the finding is withheld.
                verified[index] = WithOutcome(effectiveFinding, effectiveFinding.Evidence, CreateNoClaimOutcome(effectiveFinding));
                continue;
            }

            var claim = claims[0];
            var initialWorkItem = new VerificationWorkItem(
                claim,
                effectiveFinding.Provenance,
                claim.Stage,
                VerificationWorkItem.CrossFileScope,
                true,
                effectiveFinding.Evidence);

            EvidenceBundle evidence;
            try
            {
                evidence = await reviewEvidenceCollector.CollectEvidenceAsync(initialWorkItem, reviewContext.ReviewTools, sourceBranch, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                await this.RecordDegradedAsync(
                    protocolId,
                    new
                    {
                        findingId = effectiveFinding.FindingId,
                        claimId = claim.ClaimId,
                        stage = ClaimDescriptor.PrLevelStage,
                        degradedComponent = "evidence_collection",
                    },
                    null,
                    ex.Message,
                    ct);

                verified[index] = CreateEvidenceCollectionDegradedFinding(effectiveFinding, claim, ex.Message);
                continue;
            }

            await this.RecordEvidenceCollectedAsync(protocolId, effectiveFinding, claim, evidence, ct);

            var updatedEvidence = BuildUpdatedEvidence(effectiveFinding, evidence);
            var evidenceBackedWorkItem = new VerificationWorkItem(
                claim,
                effectiveFinding.Provenance,
                claim.Stage,
                VerificationWorkItem.CrossFileScope,
                true,
                updatedEvidence)
            {
                ProducedWithRepositoryTools = ProducedWithRepositoryTools(effectiveFinding, reviewContext),
            };

            pendingJudgements.Add(new PendingJudgement(index, effectiveFinding, evidenceBackedWorkItem, updatedEvidence));
        }

        var outcomes = await this.VerifyClaimsAsync(pendingJudgements, verificationContext, ct);
        for (var pendingIndex = 0; pendingIndex < pendingJudgements.Count; pendingIndex++)
        {
            var (index, effectiveFinding, workItem, _) = pendingJudgements[pendingIndex];
            var outcome = outcomes[pendingIndex];

            await this.RecordDecisionAsync(protocolId, outcome, workItem, ct);

            verified[index] = new CandidateReviewFinding(
                effectiveFinding.FindingId,
                effectiveFinding.Provenance,
                effectiveFinding.Severity,
                effectiveFinding.Message,
                effectiveFinding.Category,
                effectiveFinding.FilePath,
                effectiveFinding.LineNumber,
                workItem.ExistingEvidence,
                effectiveFinding.CandidateSummaryText,
                effectiveFinding.InvariantCheckContext,
                outcome,
                effectiveFinding.ScopeRelation);
        }

        return verified.Select(finding => finding!).ToList();
    }

    // Whether the pass that produced the finding could read the repository. A PR-wide pass investigates with the
    // review tools of this run, so its findings could; a synthesized cross-file finding comes from the synthesis call,
    // which runs without repository tools. The judge publishes an undecided finding only when its producer could read
    // the repository.
    private static bool ProducedWithRepositoryTools(CandidateReviewFinding finding, ReviewSystemContext reviewContext)
    {
        return reviewContext.ReviewTools is not null
               && string.Equals(finding.Provenance.OriginKind, CandidateFindingProvenance.PrWidePassOrigin, StringComparison.Ordinal);
    }

    private static VerificationOutcome CreateNoClaimOutcome(CandidateReviewFinding finding)
    {
        return new VerificationOutcome(
            $"{finding.FindingId}:claim:none",
            finding.FindingId,
            VerificationOutcome.NonVerifiableKind,
            FinalGateDecision.SummaryOnlyDisposition,
            [ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport],
            [],
            VerificationOutcome.NoEvidence,
            "No verifiable claim could be extracted from the finding, so the evidence judge could not decide it.",
            VerificationOutcome.DeterministicRulesEvaluator,
            false);
    }

    private static CandidateReviewFinding WithOutcome(CandidateReviewFinding finding, EvidenceReference? evidence, VerificationOutcome outcome)
    {
        return new CandidateReviewFinding(
            finding.FindingId,
            finding.Provenance,
            finding.Severity,
            finding.Message,
            finding.Category,
            finding.FilePath,
            finding.LineNumber,
            evidence,
            finding.CandidateSummaryText,
            finding.InvariantCheckContext,
            outcome,
            finding.ScopeRelation)
        {
            MergedFinding = finding.MergedFinding,
        };
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
            finding.VerificationOutcome,
            finding.ScopeRelation)
        {
            MergedFinding = finding.MergedFinding,
        };
    }

    // The evidence judge decides the claims with the same rule, verdicts, model tier, bounds and trace as file
    // findings, in one call for all of them. The collected evidence is recorded and kept on the finding; the judge
    // receives excerpts of the cited files and makes its own lookups. Without a judge every claim is withheld. The
    // outcomes are returned in the order of the pending judgements and matched to them by claim and finding.
    private async Task<IReadOnlyList<VerificationOutcome>> VerifyClaimsAsync(
        IReadOnlyList<PendingJudgement> pendingJudgements,
        ReviewVerificationContext verificationContext,
        CancellationToken ct)
    {
        if (pendingJudgements.Count == 0)
        {
            return [];
        }

        if (reviewFindingVerifier is null)
        {
            return pendingJudgements.Select(pending => CreateNoJudgeOutcome(pending.WorkItem.Claim, pending.UpdatedEvidence)).ToList();
        }

        var outcomes = await reviewFindingVerifier
            .VerifyAsync(pendingJudgements.Select(pending => pending.WorkItem).ToList(), invariantFacts ?? [], verificationContext, ct)
            .ConfigureAwait(false);
        var outcomesByClaim = outcomes.ToLookup(outcome => (outcome.ClaimId, outcome.FindingId));
        return pendingJudgements
            .Select(pending => SelectOutcome(
                pending.WorkItem.Claim, outcomesByClaim[(pending.WorkItem.Claim.ClaimId, pending.WorkItem.Claim.FindingId)].ToList()))
            .ToList();
    }

    // A claim with several outcomes is withheld when any of them withholds it.
    private static VerificationOutcome SelectOutcome(ClaimDescriptor claim, IReadOnlyList<VerificationOutcome> outcomes)
    {
        return outcomes.FirstOrDefault(outcome => !string.Equals(
                   outcome.RecommendedDisposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal))
               ?? outcomes.FirstOrDefault()
               ?? VerificationOutcome.DegradedUnresolved(
                   claim,
                   VerificationOutcome.AiMicroVerifierEvaluator,
                   ReviewFindingGateReasonCodes.VerificationDegraded,
                   "The evidence judge returned no outcome.");
    }

    private static VerificationOutcome CreateNoJudgeOutcome(ClaimDescriptor claim, EvidenceReference updatedEvidence)
    {
        return new VerificationOutcome(
            claim.ClaimId,
            claim.FindingId,
            VerificationOutcome.UnresolvedKind,
            FinalGateDecision.SummaryOnlyDisposition,
            [
                updatedEvidence.HasResolvedMultiFileEvidence
                    ? ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport
                    : ReviewFindingGateReasonCodes.MissingMultiFileEvidence,
            ],
            [],
            VerificationOutcome.WeakEvidence,
            "No evidence judge is available, so the claim is withheld.",
            VerificationOutcome.AiMicroVerifierEvaluator,
            false);
    }

    private async Task RecordEvidenceCollectedAsync(
        Guid? protocolId,
        CandidateReviewFinding finding,
        ClaimDescriptor claim,
        EvidenceBundle evidence,
        CancellationToken ct)
    {
        if (!protocolId.HasValue)
        {
            return;
        }

        await protocolRecorder.RecordVerificationEventAsync(
            protocolId.Value,
            ReviewProtocolEventNames.VerificationEvidenceCollected,
            JsonSerializer.Serialize(
                new
                {
                    findingId = finding.FindingId,
                    claimId = claim.ClaimId,
                    coverageState = evidence.CoverageState,
                }),
            JsonSerializer.Serialize(evidence, FinalGateJsonOptions),
            null,
            ct);
    }

    private async Task RecordDecisionAsync(
        Guid? protocolId,
        VerificationOutcome outcome,
        VerificationWorkItem workItem,
        CancellationToken ct)
    {
        if (!protocolId.HasValue)
        {
            return;
        }

        await protocolRecorder.RecordVerificationEventAsync(
            protocolId.Value,
            ReviewProtocolEventNames.VerificationPrDecision,
            JsonSerializer.Serialize(
                new
                {
                    findingId = outcome.FindingId,
                    claimId = outcome.ClaimId,
                    verifierFamilies = workItem.VerifierFamilies,
                }),
            JsonSerializer.Serialize(outcome, FinalGateJsonOptions),
            null,
            ct);
    }

    private async Task RecordDegradedAsync(
        Guid? protocolId,
        object details,
        string? output,
        string? error,
        CancellationToken ct)
    {
        if (!protocolId.HasValue)
        {
            return;
        }

        await protocolRecorder.RecordVerificationEventAsync(
            protocolId.Value,
            ReviewProtocolEventNames.VerificationDegraded,
            JsonSerializer.Serialize(details),
            output,
            error,
            ct);
    }

    private static CandidateReviewFinding CreateClaimExtractionDegradedFinding(CandidateReviewFinding finding, string? error)
    {
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
            VerificationOutcome.DegradedUnresolved(
                finding.FindingId,
                VerificationOutcome.DeterministicRulesEvaluator,
                ReviewFindingGateReasonCodes.VerificationDegraded,
                $"PR-level claim extraction degraded: {error}"),
            finding.ScopeRelation);
    }

    private static CandidateReviewFinding CreateEvidenceCollectionDegradedFinding(
        CandidateReviewFinding finding,
        ClaimDescriptor claim,
        string? error)
    {
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
            VerificationOutcome.DegradedUnresolved(
                claim,
                VerificationOutcome.AiMicroVerifierEvaluator,
                ReviewFindingGateReasonCodes.VerificationDegraded,
                $"PR-level evidence collection degraded: {error}"),
            finding.ScopeRelation);
    }

    private static EvidenceReference BuildUpdatedEvidence(CandidateReviewFinding finding, EvidenceBundle evidence)
    {
        var supportingFiles = evidence.EvidenceItems
            .Select(item => item.SourceId)
            .Where(sourceId => !string.IsNullOrWhiteSpace(sourceId))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var evidenceState = evidence.CoverageState switch
        {
            EvidenceBundle.CompleteCoverage => EvidenceReference.ResolvedState,
            EvidenceBundle.PartialCoverage => EvidenceReference.PartialState,
            _ => EvidenceReference.MissingState,
        };

        var resolvedSupportingFiles = supportingFiles.Length > 0
            ? supportingFiles
            : finding.Evidence?.SupportingFiles ?? supportingFiles;

        return finding.Evidence is null
            ? new EvidenceReference([], supportingFiles, evidenceState, "review_context_tools")
            : new EvidenceReference(
                finding.Evidence.SupportingFindingIds,
                resolvedSupportingFiles,
                evidenceState,
                finding.Evidence.EvidenceSource);
    }

    private sealed record PendingJudgement(
        int Index,
        CandidateReviewFinding Finding,
        VerificationWorkItem WorkItem,
        EvidenceReference UpdatedEvidence);
}
