// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;

/// <summary>
///     Applies deterministic contradiction checks to local verification work items.
/// </summary>
public sealed class DeterministicLocalReviewVerifier : IReviewFindingVerifier
{
    public Task<IReadOnlyList<VerificationOutcome>> VerifyAsync(
        IReadOnlyList<VerificationWorkItem> workItems,
        IReadOnlyList<InvariantFact> invariantFacts,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workItems);
        ArgumentNullException.ThrowIfNull(invariantFacts);

        var outcomes = new List<VerificationOutcome>(workItems.Count);
        foreach (var workItem in workItems)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var claim = workItem.Claim;
                if (RequiresBoundedEvidence(claim))
                {
                    outcomes.Add(CreateConservativeLocalOutcome(claim));
                    continue;
                }

                if (!InvariantFact.TryGetBlockingInvariantId(claim.ClaimKind, out var invariantId))
                {
                    outcomes.Add(
                        VerificationOutcome.Supported(
                            claim,
                            ReviewFindingGateReasonCodes.DefaultPublish,
                            "No known contradiction invariant applies to this claim family."));
                    continue;
                }

                if (invariantFacts.Any(fact => string.Equals(fact.InvariantId, invariantId, StringComparison.Ordinal)))
                {
                    outcomes.Add(
                        VerificationOutcome.Contradicted(
                            claim,
                            invariantId,
                            ReviewFindingGateReasonCodes.InvariantContradiction,
                            $"Claim kind '{claim.ClaimKind}' contradicts invariant '{invariantId}'."));
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

    private static bool RequiresBoundedEvidence(ClaimDescriptor claim)
    {
        return !string.Equals(claim.VerificationMode, ClaimDescriptor.DeterministicOnlyMode, StringComparison.Ordinal) ||
               claim.RequiresCrossFileEvidence ||
               claim.RequiresSymbolEvidence;
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
