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
            .Select(finding => EvaluateFinding(finding, invariantFacts))
            .ToList()
            .AsReadOnly();

        return Task.FromResult(decisions);
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
            if (string.Equals(finding.VerificationOutcome.RecommendedDisposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal))
            {
                return new FinalGateDecision(
                    finding.FindingId,
                    FinalGateDecision.PublishDisposition,
                    finding.VerificationOutcome.ReasonCodes,
                    "verification_outcome_rules",
                    finding.VerificationOutcome.BlockedInvariantIds,
                    finding.Evidence,
                    null);
            }

            return new FinalGateDecision(
                finding.FindingId,
                FinalGateDecision.SummaryOnlyDisposition,
                finding.VerificationOutcome.ReasonCodes,
                "verification_outcome_rules",
                finding.VerificationOutcome.BlockedInvariantIds,
                finding.Evidence,
                finding.CandidateSummaryText ??
                "Potential cross-cutting concern noted, but the available evidence did not support publication as a review thread.");
        }

        if (string.Equals(finding.Category, CandidateReviewFinding.CrossCuttingCategory, StringComparison.Ordinal))
        {
            return new FinalGateDecision(
                finding.FindingId,
                FinalGateDecision.SummaryOnlyDisposition,
                [finding.Evidence?.HasResolvedMultiFileEvidence == true
                    ? ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport
                    : ReviewFindingGateReasonCodes.MissingMultiFileEvidence],
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

        if (string.Equals(finding.Category, "non_actionable", StringComparison.Ordinal) ||
            finding.Message.StartsWith("consider ", StringComparison.OrdinalIgnoreCase))
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
