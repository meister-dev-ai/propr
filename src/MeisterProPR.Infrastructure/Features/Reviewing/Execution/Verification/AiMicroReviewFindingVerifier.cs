// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;

/// <summary>
///     Conservative PR-level verifier that promotes only independently supported cross-file claims.
/// </summary>
public sealed class AiMicroReviewFindingVerifier : IReviewFindingVerifier
{
    public Task<IReadOnlyList<VerificationOutcome>> VerifyAsync(
        IReadOnlyList<VerificationWorkItem> workItems,
        IReadOnlyList<InvariantFact> invariantFacts,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workItems);

        var outcomes = new List<VerificationOutcome>(workItems.Count);
        foreach (var workItem in workItems)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                outcomes.Add(
                    new VerificationOutcome(
                        workItem.Claim.ClaimId,
                        workItem.Claim.FindingId,
                        VerificationOutcome.UnresolvedKind,
                        FinalGateDecision.SummaryOnlyDisposition,
                        [workItem.ExistingEvidence?.HasResolvedMultiFileEvidence == true
                            ? ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport
                            : ReviewFindingGateReasonCodes.MissingMultiFileEvidence],
                        [],
                        VerificationOutcome.WeakEvidence,
                        "Retrieved context is treated as a verification hint until a bounded claim outcome supports publication.",
                        VerificationOutcome.AiMicroVerifierEvaluator,
                        false));
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                outcomes.Add(
                    VerificationOutcome.DegradedUnresolved(
                        workItem.Claim,
                        VerificationOutcome.AiMicroVerifierEvaluator,
                        ReviewFindingGateReasonCodes.VerificationDegraded,
                        $"AI micro-verification degraded: {ex.Message}"));
            }
        }

        return Task.FromResult<IReadOnlyList<VerificationOutcome>>(outcomes);
    }
}
