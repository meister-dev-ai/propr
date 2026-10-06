// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;

/// <summary>
///     Runs the deterministic verifier first, then sends every claim it withheld for lack of bounded evidence to the
///     evidence-backed judge. Local verification marks every file-pass finding as needing evidence, so every claim that
///     no invariant fact contradicts reaches the judge. The other deterministic outcomes (invariant contradiction,
///     degraded, and objective support for a claim that needs no evidence) are kept unchanged.
/// </summary>
public sealed class CompositeReviewFindingVerifier(
    DeterministicLocalReviewVerifier deterministicVerifier,
    EvidenceBackedReviewVerifier evidenceVerifier) : IReviewFindingVerifier
{
    public async Task<IReadOnlyList<VerificationOutcome>> VerifyAsync(
        IReadOnlyList<VerificationWorkItem> workItems,
        IReadOnlyList<InvariantFact> invariantFacts,
        ReviewVerificationContext? verificationContext = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workItems);

        var baseOutcomes = await deterministicVerifier
            .VerifyAsync(workItems, invariantFacts, verificationContext, ct)
            .ConfigureAwait(false);

        // Without an evidence channel nothing can be escalated. The deterministic withholds stand, and each is marked as a
        // claim the judge could not decide, so the outage is visible in the trace.
        if (verificationContext?.Tools is null || (verificationContext.ChatClient is null && verificationContext.Resolver is null))
        {
            return baseOutcomes
                .Select(outcome => IsConservativeWithhold(outcome)
                    ? outcome with { JudgeDegradation = EvidenceJudgeDegradations.Unavailable }
                    : outcome)
                .ToList();
        }

        var workItemsByClaimId = new Dictionary<string, VerificationWorkItem>(StringComparer.Ordinal);
        foreach (var workItem in workItems)
        {
            workItemsByClaimId[workItem.Claim.ClaimId] = workItem;
        }

        var withheld = baseOutcomes
            .Where(IsConservativeWithhold)
            .Select(outcome => workItemsByClaimId.GetValueOrDefault(outcome.ClaimId))
            .OfType<VerificationWorkItem>()
            .ToList();
        if (withheld.Count == 0)
        {
            return baseOutcomes;
        }

        var escalated = await evidenceVerifier
            .VerifyAsync(withheld, invariantFacts, verificationContext, ct)
            .ConfigureAwait(false);

        // The escalated outcome replaces the deterministic withhold whether or not the judge confirmed:
        // a non-confirming escalation keeps the same SummaryOnly disposition but carries the AiMicro
        // evaluator and the judge's reason, so the recorded local decision shows that escalation actually
        // ran and why it did not promote. Dispositions never regress relative to the deterministic pass.
        var escalatedByClaimId = escalated
            .GroupBy(outcome => outcome.ClaimId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        if (escalatedByClaimId.Count == 0)
        {
            return baseOutcomes;
        }

        return baseOutcomes
            .Select(outcome => IsConservativeWithhold(outcome) && escalatedByClaimId.TryGetValue(outcome.ClaimId, out var replacement)
                ? replacement
                : outcome)
            .ToList();
    }

    private static bool IsConservativeWithhold(VerificationOutcome outcome)
    {
        return string.Equals(outcome.OutcomeKind, VerificationOutcome.NonVerifiableKind, StringComparison.Ordinal)
               && string.Equals(outcome.RecommendedDisposition, FinalGateDecision.SummaryOnlyDisposition, StringComparison.Ordinal)
               && outcome.ReasonCodes.Contains(ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport, StringComparer.Ordinal);
    }
}
