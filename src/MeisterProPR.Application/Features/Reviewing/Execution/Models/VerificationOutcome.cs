// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Verification result for one extracted claim.
/// </summary>
public sealed record VerificationOutcome
{
    public const string SupportedKind = "Supported";
    public const string ContradictedKind = "Contradicted";
    public const string UnresolvedKind = "Unresolved";
    public const string InsufficientEvidenceKind = "InsufficientEvidence";
    public const string NonVerifiableKind = "NonVerifiable";
    public const string NotApplicableKind = "NotApplicable";
    public const string StrongEvidence = "Strong";
    public const string ModerateEvidence = "Moderate";
    public const string WeakEvidence = "Weak";
    public const string NoEvidence = "None";
    public const string DeterministicRulesEvaluator = "DeterministicRules";
    public const string AiMicroVerifierEvaluator = "AiMicroVerifier";
    public const string CombinedEvaluator = "Combined";

    public VerificationOutcome(
        string claimId,
        string findingId,
        string outcomeKind,
        string recommendedDisposition,
        IReadOnlyList<string>? reasonCodes,
        IReadOnlyList<string>? blockedInvariantIds,
        string evidenceStrength,
        string? evidenceSummary,
        string evaluatedBy,
        bool degraded)
    {
        if (string.IsNullOrWhiteSpace(claimId))
        {
            throw new ArgumentException("Claim ID is required.", nameof(claimId));
        }

        if (string.IsNullOrWhiteSpace(findingId))
        {
            throw new ArgumentException("Finding ID is required.", nameof(findingId));
        }

        if (string.IsNullOrWhiteSpace(outcomeKind))
        {
            throw new ArgumentException("Outcome kind is required.", nameof(outcomeKind));
        }

        if (string.IsNullOrWhiteSpace(recommendedDisposition))
        {
            throw new ArgumentException("Recommended disposition is required.", nameof(recommendedDisposition));
        }

        if (string.IsNullOrWhiteSpace(evaluatedBy))
        {
            throw new ArgumentException("EvaluatedBy is required.", nameof(evaluatedBy));
        }

        this.ClaimId = claimId;
        this.FindingId = findingId;
        this.OutcomeKind = outcomeKind;
        this.RecommendedDisposition = recommendedDisposition;
        this.ReasonCodes = reasonCodes?.Where(code => !string.IsNullOrWhiteSpace(code)).ToArray() ?? [];
        this.BlockedInvariantIds = blockedInvariantIds?.Where(id => !string.IsNullOrWhiteSpace(id)).ToArray() ?? [];
        this.EvidenceStrength = string.IsNullOrWhiteSpace(evidenceStrength) ? NoEvidence : evidenceStrength;
        this.EvidenceSummary = evidenceSummary;
        this.EvaluatedBy = evaluatedBy;
        this.Degraded = degraded;
    }

    public string ClaimId { get; }

    public string FindingId { get; }

    public string OutcomeKind { get; }

    public string RecommendedDisposition { get; }

    public IReadOnlyList<string> ReasonCodes { get; }

    public IReadOnlyList<string> BlockedInvariantIds { get; }

    public string EvidenceStrength { get; }

    public string? EvidenceSummary { get; }

    public string EvaluatedBy { get; }

    public bool Degraded { get; }

    public bool BlocksPublication => string.Equals(this.RecommendedDisposition, FinalGateDecision.DropDisposition, StringComparison.Ordinal);

    public static VerificationOutcome Supported(ClaimDescriptor claim, string reasonCode, string evidenceSummary)
    {
        ArgumentNullException.ThrowIfNull(claim);

        return new VerificationOutcome(
            claim.ClaimId,
            claim.FindingId,
            SupportedKind,
            FinalGateDecision.PublishDisposition,
            [reasonCode],
            [],
            StrongEvidence,
            evidenceSummary,
            DeterministicRulesEvaluator,
            false);
    }

    public static VerificationOutcome Contradicted(ClaimDescriptor claim, string blockedInvariantId, string reasonCode, string evidenceSummary)
    {
        ArgumentNullException.ThrowIfNull(claim);

        return new VerificationOutcome(
            claim.ClaimId,
            claim.FindingId,
            ContradictedKind,
            FinalGateDecision.DropDisposition,
            [reasonCode],
            [blockedInvariantId],
            StrongEvidence,
            evidenceSummary,
            DeterministicRulesEvaluator,
            false);
    }

    public static VerificationOutcome DegradedUnresolved(
        ClaimDescriptor claim,
        string evaluator,
        string reasonCode,
        string evidenceSummary)
    {
        ArgumentNullException.ThrowIfNull(claim);

        return DegradedUnresolved(
            claim.FindingId,
            evaluator,
            reasonCode,
            evidenceSummary,
            claim.ClaimId);
    }

    public static VerificationOutcome DegradedUnresolved(
        string findingId,
        string evaluator,
        string reasonCode,
        string evidenceSummary,
        string? claimId = null)
    {
        if (string.IsNullOrWhiteSpace(findingId))
        {
            throw new ArgumentException("Finding ID is required.", nameof(findingId));
        }

        if (string.IsNullOrWhiteSpace(evaluator))
        {
            throw new ArgumentException("Evaluator is required.", nameof(evaluator));
        }

        return new VerificationOutcome(
            string.IsNullOrWhiteSpace(claimId) ? $"{findingId}:claim:degraded" : claimId,
            findingId,
            UnresolvedKind,
            FinalGateDecision.SummaryOnlyDisposition,
            [reasonCode],
            [],
            NoEvidence,
            evidenceSummary,
            evaluator,
            true);
    }
}
