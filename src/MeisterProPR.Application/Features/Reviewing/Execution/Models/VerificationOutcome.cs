// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Verification result for one extracted claim.
/// </summary>
public sealed record VerificationOutcome
{
    /// <summary>
    ///     Outcome kind used when the claim is supported.
    /// </summary>
    public const string SupportedKind = "Supported";

    /// <summary>
    ///     Outcome kind used when the claim is contradicted.
    /// </summary>
    public const string ContradictedKind = "Contradicted";

    /// <summary>
    ///     Outcome kind used when the claim remains unresolved.
    /// </summary>
    public const string UnresolvedKind = "Unresolved";

    /// <summary>
    ///     Outcome kind used when evidence is insufficient.
    /// </summary>
    public const string InsufficientEvidenceKind = "InsufficientEvidence";

    /// <summary>
    ///     Outcome kind used when the claim cannot be verified.
    /// </summary>
    public const string NonVerifiableKind = "NonVerifiable";

    /// <summary>
    ///     Outcome kind used when the claim does not apply.
    /// </summary>
    public const string NotApplicableKind = "NotApplicable";

    /// <summary>
    ///     Evidence strength used for strong support.
    /// </summary>
    public const string StrongEvidence = "Strong";

    /// <summary>
    ///     Evidence strength used for moderate support.
    /// </summary>
    public const string ModerateEvidence = "Moderate";

    /// <summary>
    ///     Evidence strength used for weak support.
    /// </summary>
    public const string WeakEvidence = "Weak";

    /// <summary>
    ///     Evidence strength used when no evidence is available.
    /// </summary>
    public const string NoEvidence = "None";

    /// <summary>
    ///     Evaluator name used for deterministic rules.
    /// </summary>
    public const string DeterministicRulesEvaluator = "DeterministicRules";

    /// <summary>
    ///     Evaluator name used for AI micro-verification.
    /// </summary>
    public const string AiMicroVerifierEvaluator = "AiMicroVerifier";

    /// <summary>
    ///     Evaluator name used when multiple evaluators contributed.
    /// </summary>
    public const string CombinedEvaluator = "Combined";

    /// <summary>
    ///     Initializes the verification result for a claim.
    /// </summary>
    /// <param name="claimId">Identifier of the verified claim.</param>
    /// <param name="findingId">Identifier of the source finding.</param>
    /// <param name="outcomeKind">Verification outcome kind.</param>
    /// <param name="recommendedDisposition">Recommended final disposition.</param>
    /// <param name="reasonCodes">Reason codes emitted by verification.</param>
    /// <param name="blockedInvariantIds">Blocking invariants emitted by verification.</param>
    /// <param name="evidenceStrength">Normalized evidence strength.</param>
    /// <param name="evidenceSummary">Optional evidence summary.</param>
    /// <param name="evaluatedBy">Evaluator that produced the outcome.</param>
    /// <param name="degraded">Whether the outcome was produced in a degraded state.</param>
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

    /// <summary>
    ///     Gets the identifier of the verified claim.
    /// </summary>
    public string ClaimId { get; }

    /// <summary>
    ///     Gets the identifier of the source finding.
    /// </summary>
    public string FindingId { get; }

    /// <summary>
    ///     Gets the verification outcome kind.
    /// </summary>
    public string OutcomeKind { get; }

    /// <summary>
    ///     Gets the recommended final disposition.
    /// </summary>
    public string RecommendedDisposition { get; }

    /// <summary>
    ///     Gets the reason codes emitted by verification.
    /// </summary>
    public IReadOnlyList<string> ReasonCodes { get; }

    /// <summary>
    ///     Gets the blocking invariants emitted by verification.
    /// </summary>
    public IReadOnlyList<string> BlockedInvariantIds { get; }

    /// <summary>
    ///     Gets the normalized evidence strength.
    /// </summary>
    public string EvidenceStrength { get; }

    /// <summary>
    ///     Gets the optional evidence summary.
    /// </summary>
    public string? EvidenceSummary { get; }

    /// <summary>
    ///     Gets the evaluator that produced the outcome.
    /// </summary>
    public string EvaluatedBy { get; }

    /// <summary>
    ///     Gets a value indicating whether the outcome was produced in a degraded state.
    /// </summary>
    public bool Degraded { get; }

    /// <summary>
    ///     Gets a value indicating whether the recommended disposition blocks publication.
    /// </summary>
    public bool BlocksPublication => string.Equals(this.RecommendedDisposition, FinalGateDecision.DropDisposition, StringComparison.Ordinal);

    /// <summary>
    ///     Creates a supported verification outcome for the supplied claim.
    /// </summary>
    /// <param name="claim">Claim that was supported.</param>
    /// <param name="reasonCode">Reason code describing the support.</param>
    /// <param name="evidenceSummary">Evidence summary backing the support.</param>
    /// <returns>A supported verification outcome.</returns>
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

    /// <summary>
    ///     Creates a contradicted verification outcome for the supplied claim.
    /// </summary>
    /// <param name="claim">Claim that was contradicted.</param>
    /// <param name="blockedInvariantId">Blocking invariant that contradicted the claim.</param>
    /// <param name="reasonCode">Reason code describing the contradiction.</param>
    /// <param name="evidenceSummary">Evidence summary backing the contradiction.</param>
    /// <returns>A contradicted verification outcome.</returns>
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

    /// <summary>
    ///     Creates a degraded unresolved outcome for the supplied claim.
    /// </summary>
    /// <param name="claim">Claim that remained unresolved.</param>
    /// <param name="evaluator">Evaluator that reported the degraded outcome.</param>
    /// <param name="reasonCode">Reason code describing the degraded outcome.</param>
    /// <param name="evidenceSummary">Evidence summary captured during evaluation.</param>
    /// <returns>A degraded unresolved verification outcome.</returns>
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

    /// <summary>
    ///     Creates a degraded unresolved outcome when only finding-level metadata is available.
    /// </summary>
    /// <param name="findingId">Identifier of the source finding.</param>
    /// <param name="evaluator">Evaluator that reported the degraded outcome.</param>
    /// <param name="reasonCode">Reason code describing the degraded outcome.</param>
    /// <param name="evidenceSummary">Evidence summary captured during evaluation.</param>
    /// <param name="claimId">Optional identifier of the associated claim.</param>
    /// <returns>A degraded unresolved verification outcome.</returns>
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
