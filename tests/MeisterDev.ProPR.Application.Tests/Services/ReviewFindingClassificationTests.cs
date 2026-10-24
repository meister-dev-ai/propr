// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Tests.Services;

public sealed class ReviewFindingClassificationTests
{
    [Theory]
    [InlineData("defect", CommentSeverity.Error)]
    [InlineData("improvement", CommentSeverity.Suggestion)]
    [InlineData(null, CommentSeverity.Suggestion)]
    [InlineData("unverified", CommentSeverity.Suggestion)]
    public void ConfirmedVerdict_PromotesOnlyAnExplicitVerifiedDefect(string? kind, CommentSeverity expected)
    {
        var outcome = new VerificationOutcome(
            "claim", "finding", VerificationOutcome.SupportedKind, FinalGateDecision.PublishDisposition,
            [], [], VerificationOutcome.ModerateEvidence, "Bounded claim.", VerificationOutcome.AiMicroVerifierEvaluator, false)
        {
            JudgeVerdict = EvidenceJudgeVerdicts.Confirmed,
            VerifiedFindingKind = kind,
        };
        Assert.Equal(expected, ReviewFindingClassification.Resolve(CommentSeverity.Suggestion, [outcome]));
        Assert.Equal(
            kind == "improvement" ? CommentSeverity.Suggestion : CommentSeverity.Error,
            ReviewFindingClassification.Resolve(CommentSeverity.Error, [outcome]));
    }
}
