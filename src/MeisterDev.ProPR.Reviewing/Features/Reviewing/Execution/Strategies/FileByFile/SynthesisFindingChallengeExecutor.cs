// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.FileByFile;

/// <summary>Reconsiders retained findings once per stable ID with bounded synthesis counterevidence.</summary>
internal sealed class SynthesisFindingChallengeExecutor(PrLevelReviewVerificationExecutor? verifier, IProtocolRecorder recorder)
{
    internal async Task<IReadOnlyList<CandidateReviewFinding>> ApplyAsync(
        IReadOnlyList<CandidateReviewFinding> candidates,
        IReadOnlyList<FindingVerificationChallenge> challenges,
        ReviewSystemContext context,
        string sourceBranch,
        Guid? protocolId,
        IChatClient? fallback,
        Guid clientId,
        ReviewVerificationIntent intent,
        IAiRuntimeResolver? resolver,
        CancellationToken ct)
    {
        var byId = candidates.ToDictionary(candidate => candidate.FindingId, StringComparer.Ordinal);
        var requested = challenges.Take(SynthesisResponseParser.MaxFindingChallenges).DistinctBy(challenge => challenge.FindingId).ToList();
        var replacements = new Dictionary<string, CandidateReviewFinding>(StringComparer.Ordinal);
        var verificationSkipped = context.SkippedSteps.Contains(FileByFileReviewStepIds.PrVerification);
        foreach (var challenge in requested)
        {
            if (!byId.TryGetValue(challenge.FindingId, out var candidate))
            {
                await RecordAsync(challenge, null, null, "unknown_finding_id");
                continue;
            }

            CandidateReviewFinding reconsidered;
            try
            {
                var challenged = candidate with { VerificationChallenge = challenge };
                var result = verifier is null || verificationSkipped
                    ? []
                    : await verifier.ApplyAsync(
                        [challenged], context, sourceBranch, protocolId,
                        fallback, ct, clientId, intent, resolver).ConfigureAwait(false);
                reconsidered = result.SingleOrDefault(finding => finding.FindingId == candidate.FindingId)
                               ?? Unresolved(
                                   challenged, verificationSkipped
                                       ? "PR-level verification is skipped; the synthesis challenge remains unresolved."
                                       : "The synthesis challenge could not be reverified.");
                if (reconsidered.VerificationOutcome is null)
                {
                    reconsidered = Unresolved(challenged, "The synthesis challenge returned no independent verification outcome.");
                }
                else if (reconsidered.VerificationOutcome.OutcomeKind != VerificationOutcome.SupportedKind
                         && reconsidered.VerificationOutcome.OutcomeKind != VerificationOutcome.ContradictedKind
                         && !reconsidered.VerificationOutcome.BlocksPublication)
                {
                    reconsidered = reconsidered with
                    {
                        VerificationOutcome = reconsidered.VerificationOutcome with
                        {
                            // The independent outcome remains in the decision event; publication waits for challenge resolution.
                            ContradictionCheckStatus = "synthesis_challenge_unresolved",
                        },
                        RiskAssessment = ReviewFindingConfidence.Resolve(reconsidered.RiskAssessment, [reconsidered.VerificationOutcome]),
                    };
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                reconsidered = Unresolved(candidate with { VerificationChallenge = challenge }, "Reconsideration failed: " + ex.GetType().Name);
            }

            replacements[candidate.FindingId] = reconsidered;
            await RecordAsync(challenge, candidate, reconsidered, verificationSkipped ? "skipped" : "reconsidered");
        }

        return candidates.Select(candidate => replacements.GetValueOrDefault(candidate.FindingId) ?? candidate).ToArray();

        async Task RecordAsync(FindingVerificationChallenge challenge, CandidateReviewFinding? before, CandidateReviewFinding? after, string status)
        {
            if (protocolId is not { } id)
            {
                return;
            }

            await recorder.RecordVerificationEventAsync(
                id, "synthesis_finding_reconsideration",
                JsonSerializer.Serialize(new { challenge.FindingId, challenge.Reason, challenge.Counterevidence, status }),
                JsonSerializer.Serialize(
                    new
                    {
                        before = before?.VerificationOutcome, after = after?.VerificationOutcome,
                        confidenceBefore = before?.RiskAssessment, confidenceAfter = after?.RiskAssessment
                    }), null, ct).ConfigureAwait(false);
        }
    }

    private static CandidateReviewFinding Unresolved(CandidateReviewFinding candidate, string reason)
    {
        var outcome = VerificationOutcome.DegradedUnresolved(
            candidate.FindingId, VerificationOutcome.AiMicroVerifierEvaluator,
            "synthesis_challenge_unresolved", reason);
        return candidate with { VerificationOutcome = outcome, RiskAssessment = ReviewFindingConfidence.Resolve(candidate.RiskAssessment, [outcome]) };
    }
}
