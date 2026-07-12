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

        if (string.Equals(finding.Provenance.OriginKind, CandidateFindingProvenance.PrWidePassOrigin, StringComparison.Ordinal))
        {
            return EvaluatePrWideFinding(finding);
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

    // Evaluates a job-level PR-wide-pass finding. Unlike file-by-file cross-cutting findings — which always stay
    // summary-only as a precision guard — a PR-wide finding publishes when it is both earned (the bounded
    // PR-verifier recommended publication) and located (its anchor sits on or next to a changed line). The
    // verifier already weighs the retrieved evidence, so resolved evidence does NOT override a verdict that
    // declined the claim. Every other PR-wide finding stays summary-only. The invariant-contradiction drop has
    // already run before this branch, so a PR-wide finding contradicting an invariant never reaches here.
    private static FinalGateDecision EvaluatePrWideFinding(CandidateReviewFinding finding)
    {
        var hasAnchor = finding.ScopeRelation is ChangedLineRelation.OnChangedLine or ChangedLineRelation.AdjacentToChange;
        var verifierApproved = finding.VerificationOutcome is not null &&
                               string.Equals(
                                   finding.VerificationOutcome.RecommendedDisposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal);

        if (verifierApproved && hasAnchor)
        {
            return new FinalGateDecision(
                finding.FindingId,
                FinalGateDecision.PublishDisposition,
                [ReviewFindingGateReasonCodes.PrWideAnchoredVerifiedClaim],
                "pr_wide_anchored_verified_rules",
                [],
                finding.Evidence,
                null);
        }

        // Distinguish the two suppression causes for accurate triage/telemetry: a verifier-approved finding held
        // here lacks a changed-line anchor for an inline thread; every other finding is held because the bounded
        // verifier did not recommend publication.
        var reasonCode = verifierApproved
            ? ReviewFindingGateReasonCodes.PrWideMissingChangedLineAnchor
            : ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport;

        return new FinalGateDecision(
            finding.FindingId,
            FinalGateDecision.SummaryOnlyDisposition,
            [reasonCode],
            "pr_wide_summary_rules",
            [],
            finding.Evidence,
            finding.CandidateSummaryText ??
            "Potential cross-cutting concern noted, but it was not verified for publication as a review thread.");
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
