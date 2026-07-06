// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;

/// <summary>
///     Deterministic post-synthesis publication gate.
/// </summary>
public sealed class DeterministicReviewFindingGate : IDeterministicReviewFindingGate
{
    private static readonly IReadOnlySet<string> BroadWeakCategories = new HashSet<string>(StringComparer.Ordinal)
    {
        "architecture",
        "documentation",
        "test",
        "ui",
        "configuration",
        "robustness",
    };

    public Task<IReadOnlyList<FinalGateDecision>> EvaluateAsync(
        IReadOnlyList<CandidateReviewFinding> findings,
        IReadOnlyList<InvariantFact> invariantFacts,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(invariantFacts);

        IReadOnlyList<FinalGateDecision> decisions = findings
            .Select(finding => LabelOutsideChangedLines(finding, EvaluateFinding(finding, invariantFacts)))
            .ToList()
            .AsReadOnly();

        return Task.FromResult(decisions);
    }

    /// <summary>
    ///     Attaches the <see cref="ReviewFindingGateReasonCodes.OutsideChangedLines" /> reason code to a
    ///     finding whose anchor line is classified as outside the pull request's changed-line ranges. This is
    ///     a label only: the disposition, rule source, evidence, and summary text are all left unchanged, so
    ///     an outside-change finding keeps whatever publish/summary/drop decision the rules already made.
    /// </summary>
    private static FinalGateDecision LabelOutsideChangedLines(CandidateReviewFinding finding, FinalGateDecision decision)
    {
        if (finding.ScopeRelation != ChangedLineRelation.OutsideChange ||
            decision.ReasonCodes.Contains(ReviewFindingGateReasonCodes.OutsideChangedLines, StringComparer.Ordinal))
        {
            return decision;
        }

        return new FinalGateDecision(
            decision.FindingId,
            decision.Disposition,
            [.. decision.ReasonCodes, ReviewFindingGateReasonCodes.OutsideChangedLines],
            decision.RuleSource,
            decision.BlockedInvariantIds,
            decision.EvidenceSnapshot,
            decision.SummaryText);
    }

    private static FinalGateDecision EvaluateFinding(
        CandidateReviewFinding finding,
        IReadOnlyList<InvariantFact> invariantFacts)
    {
        var blockedInvariantIds = GetBlockedInvariantIds(finding, invariantFacts);
        if (blockedInvariantIds.Count > 0)
        {
            return new FinalGateDecision(
                finding.FindingId,
                FinalGateDecision.DropDisposition,
                [ReviewFindingGateReasonCodes.InvariantContradiction],
                "invariant_contradiction_rules",
                blockedInvariantIds,
                finding.Evidence,
                null);
        }

        if (finding.VerificationOutcome is not null)
        {
            return EvaluateVerifiedFinding(finding);
        }

        if (finding.Provenance.RequiresExplicitSupport)
        {
            return EvaluateFindingRequiringExplicitSupport(finding);
        }

        if (string.Equals(finding.Category, CandidateReviewFinding.CrossCuttingCategory, StringComparison.Ordinal))
        {
            return new FinalGateDecision(
                finding.FindingId,
                FinalGateDecision.SummaryOnlyDisposition,
                [
                    finding.Evidence?.HasResolvedMultiFileEvidence == true
                        ? ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport
                        : ReviewFindingGateReasonCodes.MissingMultiFileEvidence,
                ],
                finding.Evidence?.HasResolvedMultiFileEvidence == true
                    ? "cross_cutting_claim_support_rules"
                    : "cross_cutting_evidence_rules",
                [],
                finding.Evidence,
                finding.CandidateSummaryText ??
                "Potential cross-cutting concern noted, but the available evidence did not support publication as a review thread.");
        }

        if (BroadWeakCategories.Contains(finding.Category))
        {
            return new FinalGateDecision(
                finding.FindingId,
                FinalGateDecision.SummaryOnlyDisposition,
                [ReviewFindingGateReasonCodes.WeakBroadFinding],
                "broad_finding_rules",
                [],
                finding.Evidence,
                finding.CandidateSummaryText ?? "Potential broad concern noted, but it did not meet the publication bar for an actionable review thread.");
        }

        if (string.Equals(finding.Category, "non_actionable", StringComparison.Ordinal))
        {
            return new FinalGateDecision(
                finding.FindingId,
                FinalGateDecision.DropDisposition,
                [ReviewFindingGateReasonCodes.NonActionableFinding],
                "non_actionable_rules",
                [],
                finding.Evidence,
                null);
        }

        return new FinalGateDecision(
            finding.FindingId,
            FinalGateDecision.PublishDisposition,
            [ReviewFindingGateReasonCodes.DefaultPublish],
            "default_publish_rules",
            [],
            finding.Evidence,
            null);
    }

    private static FinalGateDecision EvaluateVerifiedFinding(CandidateReviewFinding finding)
    {
        var verificationOutcome = finding.VerificationOutcome!;
        if (string.Equals(verificationOutcome.RecommendedDisposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal))
        {
            if (finding.Provenance.RequiresExplicitSupport)
            {
                return new FinalGateDecision(
                    finding.FindingId,
                    FinalGateDecision.PublishDisposition,
                    [ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport],
                    "investigation_verified_support_rules",
                    verificationOutcome.BlockedInvariantIds,
                    finding.Evidence,
                    null);
            }

            return new FinalGateDecision(
                finding.FindingId,
                FinalGateDecision.PublishDisposition,
                verificationOutcome.ReasonCodes,
                "verification_outcome_rules",
                verificationOutcome.BlockedInvariantIds,
                finding.Evidence,
                null);
        }

        return new FinalGateDecision(
            finding.FindingId,
            finding.Provenance.RequiresExplicitSupport ? FinalGateDecision.DropDisposition : FinalGateDecision.SummaryOnlyDisposition,
            finding.Provenance.RequiresExplicitSupport
                ? [ReviewFindingGateReasonCodes.InvestigationOriginMissingExplicitSupport]
                : verificationOutcome.ReasonCodes,
            finding.Provenance.RequiresExplicitSupport ? "investigation_missing_support_rules" : "verification_outcome_rules",
            verificationOutcome.BlockedInvariantIds,
            finding.Evidence,
            finding.Provenance.RequiresExplicitSupport
                ? null
                : finding.CandidateSummaryText ??
                  "Potential cross-cutting concern noted, but the available evidence did not support publication as a review thread.");
    }

    private static FinalGateDecision EvaluateFindingRequiringExplicitSupport(CandidateReviewFinding finding)
    {
        if (string.Equals(finding.Provenance.OriginKind, CandidateFindingProvenance.RepeatedJudgmentOrigin, StringComparison.Ordinal))
        {
            if (finding.InvariantCheckContext.TryGetValue(CandidateReviewFinding.RepeatedJudgmentAgreementStateContextKey, out var agreementState)
                && string.Equals(agreementState, "Agreed", StringComparison.Ordinal)
                && finding.InvariantCheckContext.TryGetValue(CandidateReviewFinding.RepeatedJudgmentUsedSameEvidenceSetContextKey, out var sameEvidence)
                && string.Equals(sameEvidence, bool.TrueString, StringComparison.OrdinalIgnoreCase))
            {
                return new FinalGateDecision(
                    finding.FindingId,
                    FinalGateDecision.PublishDisposition,
                    [ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport],
                    "repeated_judgment_agreement_rules",
                    [],
                    finding.Evidence,
                    null);
            }

            return new FinalGateDecision(
                finding.FindingId,
                FinalGateDecision.DropDisposition,
                [ReviewFindingGateReasonCodes.RepeatedJudgmentDisagreement],
                "repeated_judgment_disagreement_rules",
                [],
                finding.Evidence,
                null);
        }

        return new FinalGateDecision(
            finding.FindingId,
            FinalGateDecision.DropDisposition,
            [ReviewFindingGateReasonCodes.InvestigationOriginMissingExplicitSupport],
            "investigation_missing_support_rules",
            [],
            finding.Evidence,
            null);
    }

    private static IReadOnlyList<string> GetBlockedInvariantIds(
        CandidateReviewFinding finding,
        IReadOnlyList<InvariantFact> invariantFacts)
    {
        if (finding.InvariantCheckContext.Count == 0 || invariantFacts.Count == 0)
        {
            return [];
        }

        var blocked = new List<string>();

        if (finding.InvariantCheckContext.TryGetValue(CandidateReviewFinding.ClaimKindContextKey, out var claimKind) &&
            InvariantFact.TryGetBlockingInvariantId(claimKind, out var invariantId) &&
            invariantFacts.Any(fact => string.Equals(fact.InvariantId, invariantId, StringComparison.Ordinal)))
        {
            blocked.Add(invariantId);
        }

        return blocked.Distinct(StringComparer.Ordinal).ToArray();
    }
}
