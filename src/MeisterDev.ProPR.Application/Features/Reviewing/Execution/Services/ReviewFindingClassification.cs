// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>Separates verified defects from actionable improvements without interpreting prose or risk as classification.</summary>
public static class ReviewFindingClassification
{
    /// <summary>Uses explicit verified finding kinds while preserving classifications without sufficient evidence.</summary>
    public static CommentSeverity Resolve(CommentSeverity original, IReadOnlyList<VerificationOutcome> outcomes)
    {
        if (outcomes.Count == 0 || outcomes.Any(outcome => outcome.Degraded || outcome.BlocksPublication
                                                                            || outcome.OutcomeKind != VerificationOutcome.SupportedKind ||
                                                                            outcome.RecommendedDisposition != FinalGateDecision.PublishDisposition))
        {
            return original;
        }

        if (outcomes.Any(outcome => outcome.VerifiedFindingKind == "defect"
                                    && outcome.JudgeVerdict is EvidenceJudgeVerdicts.Confirmed or EvidenceJudgeVerdicts.IntendedContradicted))
        {
            return CommentSeverity.Error;
        }

        return outcomes.All(outcome => outcome.VerifiedFindingKind == "improvement"
                                       && outcome.JudgeVerdict is EvidenceJudgeVerdicts.Confirmed or EvidenceJudgeVerdicts.IntendedContradicted)
            ? CommentSeverity.Suggestion
            : original;
    }
}
