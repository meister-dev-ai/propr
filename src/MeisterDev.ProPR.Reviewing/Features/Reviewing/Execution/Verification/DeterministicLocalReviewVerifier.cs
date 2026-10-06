// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics.CodeAnalysis;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;

/// <summary>
///     Applies the deterministic contradiction check ahead of the evidence judge. A claim that a known invariant fact
///     contradicts is dropped. Every other claim is withheld for lack of bounded evidence, which hands it to the
///     evidence judge; this verifier never publishes a finding.
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
                outcomes.Add(
                    TryContradictWithInvariant(workItem.Claim, invariantFacts, out var contradiction)
                        ? contradiction
                        : CreateConservativeLocalOutcome(workItem.Claim));
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
