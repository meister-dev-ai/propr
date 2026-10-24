// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.PrWideAgentic;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution.Verification;

public sealed partial class PrWideVerificationPipelineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CrossFileVerification_IncompleteAggregateClassificationPreservesOriginalSeverity(bool reverse)
    {
        var producer = Substitute.For<IChatClient>();
        producer.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        """{"plan_id":"plan","concerns":["Review the cross-file contract."],"changed_areas":["src"],"investigation_tasks":[],"no_investigation_reason":"The source is sufficient."}""")),
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        """{"summary":"The input loses the result.","candidate_findings":[{"id":"finding","message":"The supplied input loses the result.","severity":"warning","category":"cross_cutting","candidate_summary_text":"The result is lost.","confidence":{"concern":"cross_file_reasoning","score":90},"supporting_files":["src/Core/Aggregator.cs","src/Api/PublishController.cs"],"file_path":"src/Core/Aggregator.cs","line_number":1}]}""")));
        var extractor = Substitute.For<IReviewClaimExtractor>();
        extractor.ExtractClaims(Arg.Any<CandidateReviewFinding>()).Returns(call =>
        {
            var finding = call.Arg<CandidateReviewFinding>();
            var claims = new[] { "unclassified", "improvement" }.Select(kind => new ClaimDescriptor(
                kind, finding.FindingId, ClaimDescriptor.PrLevelStage, CandidateReviewFinding.GenericReviewAssertionClaimKind,
                finding.Message, finding.Severity, ClaimDescriptor.DeterministicOnlyMode, ClaimDescriptor.CodeContractFamily)).ToArray();
            return reverse ? claims.Reverse().ToArray() : claims;
        });
        var verifier = Substitute.For<IReviewFindingVerifier>();
        verifier.VerifyAsync(
            Arg.Any<IReadOnlyList<VerificationWorkItem>>(), Arg.Any<IReadOnlyList<InvariantFact>>(),
            Arg.Any<ReviewVerificationContext?>(), Arg.Any<CancellationToken>()).Returns(call =>
            call.Arg<IReadOnlyList<VerificationWorkItem>>().Select(item => new VerificationOutcome(
                item.Claim.ClaimId, item.Claim.FindingId, VerificationOutcome.SupportedKind, FinalGateDecision.PublishDisposition,
                [ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport], [], VerificationOutcome.ModerateEvidence,
                "The source supports the finding.", VerificationOutcome.AiMicroVerifierEvaluator, false)
            {
                JudgeVerdict = EvidenceJudgeVerdicts.Confirmed,
                VerifiedFindingKind = item.Claim.ClaimId == "improvement" ? "improvement" : null,
            }).ToArray());
        var sut = new PrWideAgenticReviewOrchestrator(
            Substitute.For<IFileByFileReviewOrchestrator>(), Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()),
            Substitute.For<ILogger<PrWideAgenticReviewOrchestrator>>(), new DeterministicReviewFindingGate(), [],
            extractor, new ReviewContextEvidenceCollector(), new SummaryReconciliationService(), verifier);

        var result = await sut.ReviewAsync(
            CreateJob(), CreatePr(),
            new ReviewSystemContext(null, [], CreateReviewTools(true)) { ModelId = "test-model" }, CancellationToken.None, producer);

        Assert.Equal(CommentSeverity.Warning, Assert.Single(result.Comments).Severity);
    }

    [Theory]
    [InlineData("defect", CommentSeverity.Error)]
    [InlineData("improvement", CommentSeverity.Suggestion)]
    public async Task CrossFileVerification_ClassifiesThePublishedFinding(string kind, CommentSeverity expected)
    {
        var producer = Substitute.For<IChatClient>();
        producer.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        """{"plan_id":"plan","concerns":["Review the cross-file contract."],"changed_areas":["src"],"investigation_tasks":[],"no_investigation_reason":"The supplied source is sufficient."}""")),
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        """{"summary":"The supplied input fails across the call path.","candidate_findings":[{"id":"finding","message":"The supplied input loses the published result.","severity":"suggestion","category":"cross_cutting","candidate_summary_text":"The result is lost.","confidence":{"concern":"cross_file_reasoning","score":90},"supporting_files":["src/Core/Aggregator.cs","src/Api/PublishController.cs"],"file_path":"src/Core/Aggregator.cs","line_number":1}]}""")));
        var extractor = Substitute.For<IReviewClaimExtractor>();
        extractor.ExtractClaims(Arg.Any<CandidateReviewFinding>()).Returns(call =>
        {
            var finding = call.Arg<CandidateReviewFinding>();
            return
            [
                new ClaimDescriptor(
                    "claim", finding.FindingId, ClaimDescriptor.PrLevelStage,
                    CandidateReviewFinding.GenericReviewAssertionClaimKind, finding.Message, finding.Severity,
                    ClaimDescriptor.DeterministicOnlyMode, ClaimDescriptor.CodeContractFamily)
            ];
        });
        var verifier = Substitute.For<IReviewFindingVerifier>();
        verifier.VerifyAsync(
                Arg.Any<IReadOnlyList<VerificationWorkItem>>(), Arg.Any<IReadOnlyList<InvariantFact>>(),
                Arg.Any<ReviewVerificationContext?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<IReadOnlyList<VerificationOutcome>>(
                call.Arg<IReadOnlyList<VerificationWorkItem>>().Select(item =>
                    new VerificationOutcome(
                        item.Claim.ClaimId, item.Claim.FindingId, VerificationOutcome.SupportedKind,
                        FinalGateDecision.PublishDisposition, [ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport], [],
                        VerificationOutcome.ModerateEvidence,
                        "The supplied source establishes the finding.", VerificationOutcome.AiMicroVerifierEvaluator, false)
                    {
                        JudgeVerdict = EvidenceJudgeVerdicts.Confirmed,
                        VerifiedFindingKind = kind,
                    }).ToList()));
        var context = new ReviewSystemContext(null, [], CreateReviewTools(true)) { ModelId = "test-model" };
        var sut = new PrWideAgenticReviewOrchestrator(
            Substitute.For<IFileByFileReviewOrchestrator>(),
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()),
            Substitute.For<ILogger<PrWideAgenticReviewOrchestrator>>(), new DeterministicReviewFindingGate(), [],
            extractor, new ReviewContextEvidenceCollector(), new SummaryReconciliationService(), verifier);

        var result = await sut.ReviewAsync(CreateJob(), CreatePr(), context, CancellationToken.None, producer);

        Assert.Equal(expected, Assert.Single(result.Comments).Severity);
        await verifier.Received().VerifyAsync(
            Arg.Is<IReadOnlyList<VerificationWorkItem>>(items => items.Any(item => item.EvidenceScope == VerificationWorkItem.CrossFileScope)),
            Arg.Any<IReadOnlyList<InvariantFact>>(), Arg.Any<ReviewVerificationContext?>(), Arg.Any<CancellationToken>());
    }
}
