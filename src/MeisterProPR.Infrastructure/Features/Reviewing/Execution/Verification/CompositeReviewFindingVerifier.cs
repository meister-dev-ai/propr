// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Options;
using Microsoft.Extensions.Options;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;

/// <summary>
///     Runs the deterministic verifier first, then escalates only the claims it conservatively withheld for
///     lack of bounded evidence to an evidence-gathering verifier. A withheld claim is replaced solely when the
///     evidence verifier returns a publishable outcome; every other deterministic outcome (default-publish,
///     objective support, invariant contradiction, degraded) is preserved unchanged. This strictly adds recall
///     without altering the deterministic precision behavior on any other path.
/// </summary>
public sealed class CompositeReviewFindingVerifier(
    DeterministicLocalReviewVerifier deterministicVerifier,
    EvidenceBackedReviewVerifier evidenceVerifier,
    IOptions<AiReviewOptions> options) : IReviewFindingVerifier
{
    private readonly AiReviewOptions _options = options.Value;

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

        // Flag-gated (default off): when disabled, behave exactly like the deterministic verifier.
        if (!this._options.EnableEvidenceBackedVerification)
        {
            return baseOutcomes;
        }

        // No evidence channel → nothing to escalate; keep deterministic behavior exactly.
        if (verificationContext?.Tools is null || (verificationContext.ChatClient is null && verificationContext.Resolver is null))
        {
            return baseOutcomes;
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

        var promotedByClaimId = escalated
            .Where(outcome => string.Equals(outcome.RecommendedDisposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal))
            .GroupBy(outcome => outcome.ClaimId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        if (promotedByClaimId.Count == 0)
        {
            return baseOutcomes;
        }

        return baseOutcomes
            .Select(outcome => IsConservativeWithhold(outcome) && promotedByClaimId.TryGetValue(outcome.ClaimId, out var promoted)
                ? promoted
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
