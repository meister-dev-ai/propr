// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Strategies.PrWideAgentic;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution.Verification;

public sealed partial class PrWideVerificationPipelineTests
{
    [Fact]
    public async Task ReviewAsync_MultipleCrossFileClaimsRetainUnionEvidenceAndProducerContext()
    {
        var recorder = CreateProtocolRecorder();
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {"plan_id":"plan","concerns":[],"changed_areas":[],"investigation_tasks":[],"no_investigation_reason":"Use supplied candidates."}
                                            """)), new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {"summary":"A cross-file defect.","candidate_findings":[{"id":"candidate-001","message":"Two registrations disagree.",
                                            "category":"cross_cutting","severity":"error","supporting_files":["src/Seed.cs"],
                                            "confidence":{"concern":"cross_file_reasoning","score":90},
                                            "risk_assessment":{"impact":"high","likelihood":"possible","impact_reason":"Wrong handler.","likelihood_reason":"Registration order.",
                                            "confidence":"certain","confidence_reason":"Provisional producer claim."}}]}
                                            """)));
        var extractor = Substitute.For<IReviewClaimExtractor>();
        extractor.ExtractClaims(Arg.Any<CandidateReviewFinding>()).Returns(call => Enumerable.Range(1, 2).Select(index =>
            new ClaimDescriptor(
                "claim-" + index, call.Arg<CandidateReviewFinding>().FindingId, ClaimDescriptor.PrLevelStage,
                CandidateReviewFinding.GenericReviewAssertionClaimKind, "Material claim " + index, CommentSeverity.Error,
                ClaimDescriptor.NeedsEvidenceMode, ClaimDescriptor.CrossFileConsistencyFamily)).ToArray());
        var collector = Substitute.For<IReviewEvidenceCollector>();
        collector.CollectEvidenceAsync(Arg.Any<VerificationWorkItem>(), Arg.Any<IReviewContextTools?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new EvidenceBundle(
                call.Arg<VerificationWorkItem>().Claim.ClaimId,
                [
                    new EvidenceItem(
                        "file_content", "Registration source.", call.Arg<VerificationWorkItem>().Claim.ClaimId == "claim-1" ? "src/First.cs" : "src/Second.cs")
                ],
                EvidenceBundle.CompleteCoverage));
        var requestedChecks = new List<bool>();
        var verifier = Substitute.For<IReviewFindingVerifier>();
        verifier.VerifyAsync(
                Arg.Any<IReadOnlyList<VerificationWorkItem>>(), Arg.Any<IReadOnlyList<InvariantFact>>(),
                Arg.Any<ReviewVerificationContext?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyList<VerificationWorkItem>>().Select(item =>
            {
                requestedChecks.Add(item.RequiresCertaintyCheck);
                return new VerificationOutcome(
                    item.Claim.ClaimId, item.Claim.FindingId, VerificationOutcome.SupportedKind,
                    FinalGateDecision.PublishDisposition, [ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport], [],
                    VerificationOutcome.ModerateEvidence, "Legacy source confirmation.", VerificationOutcome.AiMicroVerifierEvaluator, false);
            }).ToArray());
        var context = new ReviewSystemContext(null, [], CreateReviewTools(true))
            { ActiveProtocolId = Guid.NewGuid(), ProtocolRecorder = recorder, ModelId = "test-model" };
        var sut = new PrWideAgenticReviewOrchestrator(
            Substitute.For<IFileByFileReviewOrchestrator>(),
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()), Substitute.For<ILogger<PrWideAgenticReviewOrchestrator>>(),
            new DeterministicReviewFindingGate(), [], extractor, collector, new SummaryReconciliationService(), verifier);

        var result = await sut.ReviewAsync(CreateJob(), CreatePr(), context, CancellationToken.None, chat);

        var finding = Assert.Single(result.Comments);
        Assert.True(finding.ProducedWithRepositoryTools);
        Assert.Equal(FindingConfidence.Unknown, finding.RiskAssessment!.Confidence);
        Assert.Equal(new[] { true, true }, requestedChecks);
        await recorder.Received().RecordReviewFindingGateEventAsync(
            Arg.Any<Guid>(), ReviewProtocolEventNames.ReviewFindingGateDecision,
            Arg.Any<string?>(), Arg.Is<string?>(output => output != null && output.Contains("src/First.cs", StringComparison.Ordinal)
                                                                         && output.Contains("src/Second.cs", StringComparison.Ordinal)),
            Arg.Is<string?>(error => error == null), Arg.Any<CancellationToken>());
    }
}
