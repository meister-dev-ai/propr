// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics.CodeAnalysis;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;

/// <summary>
///     Applies deterministic contradiction checks to local verification work items.
/// </summary>
public sealed class DeterministicLocalReviewVerifier : IReviewFindingVerifier
{
    public Task<IReadOnlyList<VerificationOutcome>> VerifyAsync(
        IReadOnlyList<VerificationWorkItem> workItems,
        IReadOnlyList<InvariantFact> invariantFacts,
        ReviewVerificationContext? verificationContext = null,
        CancellationToken ct = default)
    {
        _ = verificationContext;
        ArgumentNullException.ThrowIfNull(workItems);
        ArgumentNullException.ThrowIfNull(invariantFacts);

        var outcomes = new List<VerificationOutcome>(workItems.Count);
        foreach (var workItem in workItems)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var claim = workItem.Claim;
                if (SupportsObjectiveDeterministicVerification(claim) && !RequiresBoundedEvidence(workItem))
                {
                    outcomes.Add(
                        VerificationOutcome.Supported(
                            claim,
                            ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport,
                            "Deterministic objective verification confirmed the follow-up finding."));
                    continue;
                }

                // A finding that needs evidence only because of its pass is still contradicted by a known invariant
                // fact, so the evidence verifier never sees a claim the invariants already refute.
                if (workItem.FindingProvenance.RequiresEvidenceVerification &&
                    TryContradictWithInvariant(claim, invariantFacts, out var contradiction))
                {
                    outcomes.Add(contradiction);
                    continue;
                }

                if (RequiresBoundedEvidence(workItem))
                {
                    outcomes.Add(CreateConservativeLocalOutcome(claim));
                    continue;
                }

                if (!InvariantFact.TryGetBlockingInvariantId(claim.ClaimKind, out _))
                {
                    outcomes.Add(
                        VerificationOutcome.Supported(
                            claim,
                            ReviewFindingGateReasonCodes.DefaultPublish,
                            "No known contradiction invariant applies to this claim family."));
                    continue;
                }

                if (TryContradictWithInvariant(claim, invariantFacts, out var invariantContradiction))
                {
                    outcomes.Add(invariantContradiction);
                    continue;
                }

                outcomes.Add(
                    VerificationOutcome.Supported(
                        claim,
                        ReviewFindingGateReasonCodes.DefaultPublish,
                        "No contradicting invariant fact was present."));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                outcomes.Add(
                    VerificationOutcome.DegradedUnresolved(
                        workItem.Claim,
                        VerificationOutcome.DeterministicRulesEvaluator,
                        ReviewFindingGateReasonCodes.VerificationDegraded,
                        $"Deterministic local verification degraded: {ex.Message}"));
            }
        }

        return Task.FromResult<IReadOnlyList<VerificationOutcome>>(outcomes);
    }

    private static bool TryContradictWithInvariant(
        ClaimDescriptor claim,
        IReadOnlyList<InvariantFact> invariantFacts,
        [NotNullWhen(true)] out VerificationOutcome? contradiction)
    {
        if (InvariantFact.TryGetBlockingInvariantId(claim.ClaimKind, out var invariantId) &&
            invariantFacts.Any(fact => string.Equals(fact.InvariantId, invariantId, StringComparison.Ordinal)))
        {
            contradiction = VerificationOutcome.Contradicted(
                claim,
                invariantId,
                ReviewFindingGateReasonCodes.InvariantContradiction,
                $"Claim kind '{claim.ClaimKind}' contradicts invariant '{invariantId}'.");
            return true;
        }

        contradiction = null;
        return false;
    }

    private static bool RequiresBoundedEvidence(VerificationWorkItem workItem)
    {
        return !string.Equals(workItem.Claim.VerificationMode, ClaimDescriptor.DeterministicOnlyMode, StringComparison.Ordinal) ||
               workItem.Claim.RequiresCrossFileEvidence ||
               workItem.Claim.RequiresSymbolEvidence ||
               (workItem.FindingProvenance.RequiresExplicitSupport &&
                workItem.FindingProvenance.FindingProvenanceKind == FindingProvenanceKind.ProRVOnly);
    }

    private static bool SupportsObjectiveDeterministicVerification(ClaimDescriptor claim)
    {
        return claim.ClaimKind is CandidateReviewFinding.DockerFinalStageRootUserClaimKind
            or CandidateReviewFinding.GitHubActionsSecretEchoClaimKind
            or CandidateReviewFinding.TerraformPublicIngressClaimKind
            or CandidateReviewFinding.ManifestLockfileMisalignmentClaimKind
            or CandidateReviewFinding.WiringMissingRegistrationClaimKind
            or CandidateReviewFinding.ShellUnquotedVariableClaimKind;
    }

    private static VerificationOutcome CreateConservativeLocalOutcome(ClaimDescriptor claim)
    {
        return new VerificationOutcome(
            claim.ClaimId,
            claim.FindingId,
            VerificationOutcome.NonVerifiableKind,
            FinalGateDecision.SummaryOnlyDisposition,
            [ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport],
            [],
            VerificationOutcome.NoEvidence,
            "Local deterministic verification could not independently verify this claim without bounded repository evidence.",
            VerificationOutcome.DeterministicRulesEvaluator,
            false);
    }
}
