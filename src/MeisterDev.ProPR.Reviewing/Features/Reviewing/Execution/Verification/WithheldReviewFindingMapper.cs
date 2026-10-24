// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;

internal static class WithheldReviewFindingMapper
{
    internal static string Reason(VerificationOutcome? outcome, FinalGateDecision? decision = null)
    {
        if (outcome is not null && string.Equals(outcome.RecommendedDisposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal))
        {
            outcome = null;
        }

        var reason = outcome?.JudgeDegradation
                     ?? outcome?.JudgeVerdict
                     ?? outcome?.ReasonCodes.FirstOrDefault()
                     ?? decision?.ReasonCodes.FirstOrDefault()
                     ?? "verification_withheld";
        return reason.Length <= 128 ? reason : reason[..128];
    }

    internal static IReadOnlyList<WithheldReviewFinding> Materialize(
        IReadOnlyList<CandidateReviewFinding> findings,
        IReadOnlyList<FinalGateDecision> decisions)
    {
        var byId = decisions.ToDictionary(decision => decision.FindingId, StringComparer.Ordinal);
        return findings.Where(finding => byId.TryGetValue(finding.FindingId, out var decision)
                                         && !string.Equals(decision.Disposition, FinalGateDecision.PublishDisposition, StringComparison.Ordinal))
            .Select(finding => new WithheldReviewFinding(
                new ReviewComment(finding.FilePath, finding.LineNumber, finding.Severity, finding.Message)
                {
                    RiskAssessment = finding.RiskAssessment, FindingClassification = finding.FindingClassification, SuggestedFix = finding.SuggestedFix,
                    OriginPassKind = finding.Provenance.ResolveOriginPassKindName(),
                    OriginPassIndex = finding.Provenance.UnionPassIndex,
                    OriginPassLens = finding.Provenance.UnionLens,
                    OriginPassShadow = finding.Provenance.Shadow,
                    OriginModelId = finding.Provenance.OriginModelId,
                    OriginLogicalModelName = finding.Provenance.OriginLogicalModelName,
                    OriginSymbolName = finding.Provenance.OriginSymbolName,
                    OriginSymbolKind = finding.Provenance.OriginSymbolKind,
                    ScopeRelation = ReviewCommentScopeRelationMapper.Map(finding.ScopeRelation),
                    SourceReadGrounding = finding.ReadGrounding,
                }, Reason(finding.VerificationOutcome, byId[finding.FindingId])))
            .ToList();
    }
}
