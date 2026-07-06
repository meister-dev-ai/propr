// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.PrWideAgentic;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Execution.Verification;

public sealed class PrWideVerificationPipelineTests
{
    [Fact]
    public async Task ReviewAsync_UnresolvedPrWideFinding_BecomesSummaryOnlyWithoutDelegatingToFileByFile()
    {
        var fallback = Substitute.For<IFileByFileReviewOrchestrator>();
        var protocolRecorder = CreateProtocolRecorder();
        var reviewTools = CreateReviewTools(false);
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {
                                              "plan_id": "plan-001",
                                              "concerns": ["Verify cross-file registration ordering."],
                                              "changed_areas": ["src"],
                                              "investigation_tasks": [],
                                              "no_investigation_reason": "Central synthesis can verify directly."
                                            }
                                            """)),
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {
                                              "summary": "The PR may have a cross-file registration ordering issue.",
                                              "candidate_findings": [
                                                {
                                                  "id": "candidate-001",
                                                  "message": "Cross-file registration ordering can still publish stale results.",
                                                  "severity": "warning",
                                                  "category": "cross_cutting",
                                                  "candidate_summary_text": "Potential cross-file ordering issue noted.",
                                                  "confidence": { "concern": "cross_file_reasoning", "score": 78 },
                                                  "supporting_files": ["src/Core/Aggregator.cs", "src/Api/PublishController.cs"]
                                                }
                                              ]
                                            }
                                            """)));

        var context = new ReviewSystemContext(null, [], reviewTools)
        {
            ActiveProtocolId = Guid.NewGuid(),
            ProtocolRecorder = protocolRecorder,
            ModelId = "test-model",
            PromptExperiment = new PromptExperimentContext(
                "variant-a",
                [new StagePromptVariant("pr_wide_synthesis_user", PromptStageRole.User, PromptCompositionMode.Append, "extra pr-wide synthesis guidance")]),
        };

        var sut = new PrWideAgenticReviewOrchestrator(
            fallback,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()),
            Substitute.For<ILogger<PrWideAgenticReviewOrchestrator>>(),
            new DeterministicReviewFindingGate(),
            [new DomainReviewInvariantFactProvider()],
            new DeterministicReviewClaimExtractor(),
            new ReviewContextEvidenceCollector(),
            new SummaryReconciliationService());

        var result = await sut.ReviewAsync(CreateJob(), CreatePr(), context, CancellationToken.None, chatClient);

        Assert.Empty(result.Comments);
        Assert.Contains("Potential cross-file ordering issue noted.", result.Summary);
        await fallback.DidNotReceive().ReviewAsync(
            Arg.Any<ReviewJob>(),
            Arg.Any<PullRequest>(),
            Arg.Any<ReviewSystemContext>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IChatClient?>());

        await protocolRecorder.Received().RecordPrWideStageEventAsync(
            context.ActiveProtocolId.Value,
            ReviewProtocolEventNames.PrWideVerificationCompleted,
            Arg.Is<string?>(details => details != null && details.Contains("candidate-001", StringComparison.Ordinal)),
            Arg.Is<string?>(output => output != null && output.Contains("SummaryOnly", StringComparison.Ordinal)),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());

        await protocolRecorder.Received().RecordPrWideStageEventAsync(
            context.ActiveProtocolId.Value,
            ReviewProtocolEventNames.PrWideFinalGateDecision,
            Arg.Is<string?>(details => details != null && details.Contains("candidate-001", StringComparison.Ordinal)),
            Arg.Is<string?>(output => output != null && output.Contains("SummaryOnly", StringComparison.Ordinal)),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());

        await protocolRecorder.Received().RecordPrWideStageEventAsync(
            context.ActiveProtocolId.Value,
            ReviewProtocolEventNames.PrWidePublicationPrepared,
            Arg.Any<string?>(),
            Arg.Is<string?>(output => output != null && output.Contains("\"summaryOnlyCount\":1", StringComparison.Ordinal)),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());

        await protocolRecorder.Received().RecordPromptStageEvidenceAsync(
            context.ActiveProtocolId.Value,
            "pr_wide_planning_system",
            Arg.Is<string>(variantName => variantName == "variant-a"),
            Arg.Is(PromptCompositionMode.Default),
            Arg.Is(true),
            Arg.Is<string?>(text =>
                !string.IsNullOrWhiteSpace(text) && text.Contains("Stage A of a PR-wide agentic review workflow", StringComparison.Ordinal)),
            Arg.Is<string?>(text => text == null),
            Arg.Any<CancellationToken>());
        await protocolRecorder.Received().RecordPromptStageEvidenceAsync(
            context.ActiveProtocolId.Value,
            "pr_wide_planning_user",
            Arg.Is<string>(variantName => variantName == "variant-a"),
            Arg.Is(PromptCompositionMode.Default),
            Arg.Is(true),
            Arg.Is<string?>(text => text == null),
            Arg.Is<string?>(text => !string.IsNullOrWhiteSpace(text) && text.Contains("Changed file manifest:", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await protocolRecorder.Received().RecordPromptStageEvidenceAsync(
            context.ActiveProtocolId.Value,
            "pr_wide_synthesis_system",
            Arg.Is<string>(variantName => variantName == "variant-a"),
            Arg.Is(PromptCompositionMode.Default),
            Arg.Is(true),
            Arg.Is<string?>(text =>
                !string.IsNullOrWhiteSpace(text) && text.Contains("Stage C of a PR-wide agentic review workflow", StringComparison.Ordinal)),
            Arg.Is<string?>(text => text == null),
            Arg.Any<CancellationToken>());
        await protocolRecorder.Received().RecordPromptStageEvidenceAsync(
            context.ActiveProtocolId.Value,
            "pr_wide_synthesis_user",
            Arg.Is<string>(variantName => variantName == "variant-a"),
            Arg.Is(PromptCompositionMode.Append),
            Arg.Is(false),
            Arg.Is<string?>(text => text == null),
            Arg.Is<string?>(text => !string.IsNullOrWhiteSpace(text) && text.Contains("Investigation outputs:", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_SupportedPrWideFinding_RemainsSummaryOnlyWithoutExplicitBoundedVerificationSupport()
    {
        var fallback = Substitute.For<IFileByFileReviewOrchestrator>();
        var protocolRecorder = CreateProtocolRecorder();
        var reviewTools = CreateReviewTools(true);
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {
                                              "plan_id": "plan-001",
                                              "concerns": ["Verify cross-file registration ordering."],
                                              "changed_areas": ["src"],
                                              "investigation_tasks": [],
                                              "no_investigation_reason": "Central synthesis can verify directly."
                                            }
                                            """)),
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {
                                              "summary": "The PR introduces a cross-file publication ordering risk.",
                                              "candidate_findings": [
                                                {
                                                  "id": "candidate-001",
                                                  "message": "Cross-file registration ordering can still publish stale results.",
                                                  "severity": "warning",
                                                  "category": "cross_cutting",
                                                  "candidate_summary_text": "Potential cross-file ordering issue noted.",
                                                  "confidence": { "concern": "cross_file_reasoning", "score": 86 },
                                                  "supporting_files": ["src/Core/Aggregator.cs", "src/Api/PublishController.cs"]
                                                }
                                              ]
                                            }
                                            """)));

        var context = new ReviewSystemContext(null, [], reviewTools)
        {
            ActiveProtocolId = Guid.NewGuid(),
            ProtocolRecorder = protocolRecorder,
            ModelId = "test-model",
        };

        var sut = new PrWideAgenticReviewOrchestrator(
            fallback,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()),
            Substitute.For<ILogger<PrWideAgenticReviewOrchestrator>>(),
            new DeterministicReviewFindingGate(),
            [new DomainReviewInvariantFactProvider()],
            new DeterministicReviewClaimExtractor(),
            new ReviewContextEvidenceCollector(),
            new SummaryReconciliationService());

        var result = await sut.ReviewAsync(CreateJob(), CreatePr(), context, CancellationToken.None, chatClient);

        Assert.Empty(result.Comments);
        Assert.Contains("Potential cross-file ordering issue noted.", result.Summary);
        await fallback.DidNotReceive().ReviewAsync(
            Arg.Any<ReviewJob>(),
            Arg.Any<PullRequest>(),
            Arg.Any<ReviewSystemContext>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IChatClient?>());

        await protocolRecorder.Received().RecordPrWideStageEventAsync(
            context.ActiveProtocolId.Value,
            ReviewProtocolEventNames.PrWideVerificationCompleted,
            Arg.Is<string?>(details => details != null && details.Contains("candidate-001", StringComparison.Ordinal)),
            Arg.Is<string?>(output => output != null && output.Contains("SummaryOnly", StringComparison.Ordinal)),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());

        await protocolRecorder.Received().RecordPrWideStageEventAsync(
            context.ActiveProtocolId.Value,
            ReviewProtocolEventNames.PrWidePublicationPrepared,
            Arg.Any<string?>(),
            Arg.Is<string?>(output => output != null && output.Contains("\"summaryOnlyCount\":1", StringComparison.Ordinal)),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_LocalInvariantContradiction_BecomesSummaryOnlyAfterNativeVerification()
    {
        var fallback = Substitute.For<IFileByFileReviewOrchestrator>();
        var protocolRecorder = CreateProtocolRecorder();
        var reviewTools = CreateReviewTools(true);
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {
                                              "plan_id": "plan-001",
                                              "concerns": ["Validate anchored local findings."],
                                              "changed_areas": ["src"],
                                              "investigation_tasks": [],
                                              "no_investigation_reason": "Central synthesis can verify directly."
                                            }
                                            """)),
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {
                                              "summary": "The PR may introduce a local nullability issue.",
                                              "candidate_findings": [
                                                {
                                                  "id": "candidate-001",
                                                  "message": "ReviewComment.Message may be null when the model omits a message.",
                                                  "severity": "warning",
                                                  "category": "per_file_comment",
                                                  "candidate_summary_text": "Potential local nullability concern noted.",
                                                  "confidence": { "concern": "local_reasoning", "score": 88 },
                                                  "supporting_files": ["src/Core/Aggregator.cs"],
                                                  "file_path": "src/Core/Aggregator.cs",
                                                  "line_number": 1
                                                }
                                              ]
                                            }
                                            """)));

        var context = new ReviewSystemContext(null, [], reviewTools)
        {
            ActiveProtocolId = Guid.NewGuid(),
            ProtocolRecorder = protocolRecorder,
            ModelId = "test-model",
        };

        var sut = new PrWideAgenticReviewOrchestrator(
            fallback,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()),
            Substitute.For<ILogger<PrWideAgenticReviewOrchestrator>>(),
            new DeterministicReviewFindingGate(),
            [new DomainReviewInvariantFactProvider()],
            new DeterministicReviewClaimExtractor(),
            new ReviewContextEvidenceCollector(),
            new SummaryReconciliationService(),
            new DeterministicLocalReviewVerifier());

        var result = await sut.ReviewAsync(CreateJob(), CreatePr(), context, CancellationToken.None, chatClient);

        Assert.Empty(result.Comments);
        Assert.DoesNotContain("ReviewComment.Message may be null", result.Summary, StringComparison.Ordinal);
        await fallback.DidNotReceive().ReviewAsync(
            Arg.Any<ReviewJob>(),
            Arg.Any<PullRequest>(),
            Arg.Any<ReviewSystemContext>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IChatClient?>());

        await protocolRecorder.Received().RecordVerificationEventAsync(
            context.ActiveProtocolId.Value,
            ReviewProtocolEventNames.VerificationLocalDecision,
            Arg.Any<string?>(),
            Arg.Is<string?>(output => output != null && output.Contains("\"recommendedDisposition\":\"Drop\"", StringComparison.Ordinal)),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());

        await protocolRecorder.Received().RecordPrWideStageEventAsync(
            context.ActiveProtocolId.Value,
            ReviewProtocolEventNames.PrWideFinalGateDecision,
            Arg.Is<string?>(details => details != null && details.Contains("candidate-001", StringComparison.Ordinal)),
            Arg.Is<string?>(output => output != null && output.Contains("\"finalDisposition\":\"SummaryOnly\"", StringComparison.Ordinal)),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());

        await protocolRecorder.Received().RecordPrWideStageEventAsync(
            context.ActiveProtocolId.Value,
            ReviewProtocolEventNames.PrWidePublicationPrepared,
            Arg.Any<string?>(),
            Arg.Is<string?>(output =>
                output != null
                && output.Contains("\"summaryOnlyCount\":1", StringComparison.Ordinal)
                && output.Contains("\"droppedCount\":0", StringComparison.Ordinal)),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_SpeculativePrWideFinding_IsWithheldFromPublicationWhenUnverified()
    {
        // Speculative cross-cutting findings are no longer hard-dropped by deterministic phrase screening.
        // They flow to evidence-backed verification, which withholds an unsupported claim to summary-only
        // (missing_verified_claim_support) so it is not published as an actionable comment.
        var fallback = Substitute.For<IFileByFileReviewOrchestrator>();
        var protocolRecorder = CreateProtocolRecorder();
        var reviewTools = CreateReviewTools(true);
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {
                                              "plan_id": "plan-001",
                                              "concerns": ["Filter speculative candidates."],
                                              "changed_areas": ["src"],
                                              "investigation_tasks": [],
                                              "no_investigation_reason": "Central synthesis can verify directly."
                                            }
                                            """)),
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {
                                              "summary": "The PR may have a speculative concern.",
                                              "candidate_findings": [
                                                {
                                                  "id": "candidate-001",
                                                  "message": "This may be a bug in the registration flow.",
                                                  "severity": "warning",
                                                  "category": "cross_cutting",
                                                  "candidate_summary_text": "Potential speculative concern noted.",
                                                  "confidence": { "concern": "cross_file_reasoning", "score": 70 },
                                                  "supporting_files": ["src/Core/Aggregator.cs", "src/Api/PublishController.cs"]
                                                }
                                              ]
                                            }
                                            """)));

        var context = new ReviewSystemContext(null, [], reviewTools)
        {
            ActiveProtocolId = Guid.NewGuid(),
            ProtocolRecorder = protocolRecorder,
            ModelId = "test-model",
        };

        var sut = new PrWideAgenticReviewOrchestrator(
            fallback,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()),
            Substitute.For<ILogger<PrWideAgenticReviewOrchestrator>>(),
            new DeterministicReviewFindingGate(),
            [new DomainReviewInvariantFactProvider()],
            new DeterministicReviewClaimExtractor(),
            new ReviewContextEvidenceCollector(),
            new SummaryReconciliationService(),
            new DeterministicLocalReviewVerifier());

        var result = await sut.ReviewAsync(CreateJob(), CreatePr(), context, CancellationToken.None, chatClient);

        Assert.Empty(result.Comments);
        Assert.DoesNotContain("This may be a bug", result.Summary, StringComparison.Ordinal);

        await protocolRecorder.Received().RecordPrWideStageEventAsync(
            context.ActiveProtocolId.Value,
            ReviewProtocolEventNames.PrWideVerificationCompleted,
            Arg.Is<string?>(details => details != null && details.Contains("candidate-001", StringComparison.Ordinal)),
            Arg.Is<string?>(output => output != null && output.Contains("\"recommendedDisposition\":\"SummaryOnly\"", StringComparison.Ordinal)),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WhenClaimExtractionReturnsNoClaims_RetainsFindingAsSummaryOnly()
    {
        var fallback = Substitute.For<IFileByFileReviewOrchestrator>();
        var protocolRecorder = CreateProtocolRecorder();
        var reviewTools = CreateReviewTools(true);
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {
                                              "plan_id": "plan-001",
                                              "concerns": ["Check robustness concerns."],
                                              "changed_areas": ["src"],
                                              "investigation_tasks": [],
                                              "no_investigation_reason": "Central synthesis can verify directly."
                                            }
                                            """)),
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {
                                              "summary": "The PR may mishandle completion timestamps.",
                                              "candidate_findings": [
                                                {
                                                  "id": "candidate-001",
                                                  "message": "Completion timestamps can move backward.",
                                                  "severity": "warning",
                                                  "category": "robustness",
                                                  "candidate_summary_text": "Potential timestamp ordering issue noted.",
                                                  "confidence": { "concern": "runtime_state", "score": 84 },
                                                  "supporting_files": ["src/Core/Aggregator.cs"],
                                                  "file_path": "src/Core/Aggregator.cs",
                                                  "line_number": 1
                                                }
                                              ]
                                            }
                                            """)));

        var context = new ReviewSystemContext(null, [], reviewTools)
        {
            ActiveProtocolId = Guid.NewGuid(),
            ProtocolRecorder = protocolRecorder,
            ModelId = "test-model",
        };

        var sut = new PrWideAgenticReviewOrchestrator(
            fallback,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()),
            Substitute.For<ILogger<PrWideAgenticReviewOrchestrator>>(),
            new DeterministicReviewFindingGate(),
            [new DomainReviewInvariantFactProvider()],
            new EmptyClaimExtractor(),
            new ReviewContextEvidenceCollector(),
            new SummaryReconciliationService(),
            new DeterministicLocalReviewVerifier());

        var result = await sut.ReviewAsync(CreateJob(), CreatePr(), context, CancellationToken.None, chatClient);

        Assert.Empty(result.Comments);
        Assert.Contains("Potential timestamp ordering issue noted.", result.Summary);

        await protocolRecorder.Received().RecordPrWideStageEventAsync(
            context.ActiveProtocolId.Value,
            ReviewProtocolEventNames.PrWideVerificationCompleted,
            Arg.Is<string?>(details => details != null && details.Contains("candidate-001", StringComparison.Ordinal)),
            Arg.Is<string?>(output =>
                output != null
                && output.Contains("\"outcomeKind\":\"NonVerifiable\"", StringComparison.Ordinal)
                && output.Contains("\"recommendedDisposition\":\"SummaryOnly\"", StringComparison.Ordinal)
                && output.Contains(ReviewFindingGateReasonCodes.MissingVerifiedClaimSupport, StringComparison.Ordinal)),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_CrossFileClaimOnSingleFileFinding_IsRoutedToVerificationNotScreeningDropped()
    {
        // A single-file finding whose wording trips the "unverifiable cross-file claim" screen (here via the
        // cross-file term "elsewhere") must not be dropped by deterministic screening ahead of verification.
        // Because verifiability is the only objection, the finding is routed onward; with no extractable
        // claims it is conservatively retained as summary-only rather than silently discarded. Without the
        // routing, screening would drop it and its summary text would never reach the review summary.
        var fallback = Substitute.For<IFileByFileReviewOrchestrator>();
        var protocolRecorder = CreateProtocolRecorder();
        var reviewTools = CreateReviewTools(true);
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {
                                              "plan_id": "plan-001",
                                              "concerns": ["Check anchored local findings."],
                                              "changed_areas": ["src"],
                                              "investigation_tasks": [],
                                              "no_investigation_reason": "Central synthesis can verify directly."
                                            }
                                            """)),
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {
                                              "summary": "The PR may leave a computation unguarded.",
                                              "candidate_findings": [
                                                {
                                                  "id": "candidate-001",
                                                  "message": "Values assigned elsewhere feed this computation without a guard.",
                                                  "severity": "warning",
                                                  "category": "robustness",
                                                  "candidate_summary_text": "Potential unguarded computation noted.",
                                                  "confidence": { "concern": "runtime_state", "score": 84 },
                                                  "supporting_files": ["src/Core/Aggregator.cs"],
                                                  "file_path": "src/Core/Aggregator.cs",
                                                  "line_number": 1
                                                }
                                              ]
                                            }
                                            """)));

        var context = new ReviewSystemContext(null, [], reviewTools)
        {
            ActiveProtocolId = Guid.NewGuid(),
            ProtocolRecorder = protocolRecorder,
            ModelId = "test-model",
        };

        var sut = new PrWideAgenticReviewOrchestrator(
            fallback,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()),
            Substitute.For<ILogger<PrWideAgenticReviewOrchestrator>>(),
            new DeterministicReviewFindingGate(),
            [new DomainReviewInvariantFactProvider()],
            new EmptyClaimExtractor(),
            new ReviewContextEvidenceCollector(),
            new SummaryReconciliationService(),
            new DeterministicLocalReviewVerifier());

        var result = await sut.ReviewAsync(CreateJob(), CreatePr(), context, CancellationToken.None, chatClient);

        // Retained as summary-only after verification — a screening drop would remove the summary text and
        // record a Drop disposition instead.
        Assert.Empty(result.Comments);
        Assert.Contains("Potential unguarded computation noted.", result.Summary);

        await protocolRecorder.Received().RecordPrWideStageEventAsync(
            context.ActiveProtocolId.Value,
            ReviewProtocolEventNames.PrWideVerificationCompleted,
            Arg.Is<string?>(details => details != null && details.Contains("candidate-001", StringComparison.Ordinal)),
            Arg.Is<string?>(output => output != null && output.Contains("\"recommendedDisposition\":\"SummaryOnly\"", StringComparison.Ordinal)),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_TrimmedSeverityText_PreservesExplicitSeverity()
    {
        var fallback = Substitute.For<IFileByFileReviewOrchestrator>();
        var protocolRecorder = CreateProtocolRecorder();
        var reviewTools = CreateReviewTools(true);
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {
                                              "plan_id": "plan-001",
                                              "concerns": ["Check anchored local findings."],
                                              "changed_areas": ["src"],
                                              "investigation_tasks": [],
                                              "no_investigation_reason": "Central synthesis can verify directly."
                                            }
                                            """)),
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {
                                              "summary": "The PR can publish a null review comment message.",
                                              "candidate_findings": [
                                                {
                                                  "id": "candidate-001",
                                                  "message": "ReviewComment.Message may be null when the model omits a message.",
                                                  "severity": " Error ",
                                                  "category": "per_file_comment",
                                                  "candidate_summary_text": "Potential local nullability concern noted.",
                                                  "confidence": { "concern": "local_reasoning", "score": 88 },
                                                  "supporting_files": ["src/Core/Aggregator.cs"],
                                                  "file_path": "src/Core/Aggregator.cs",
                                                  "line_number": 1
                                                }
                                              ]
                                            }
                                            """)));

        var context = new ReviewSystemContext(null, [], reviewTools)
        {
            ActiveProtocolId = Guid.NewGuid(),
            ProtocolRecorder = protocolRecorder,
            ModelId = "test-model",
        };

        var sut = new PrWideAgenticReviewOrchestrator(
            fallback,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()),
            Substitute.For<ILogger<PrWideAgenticReviewOrchestrator>>(),
            new DeterministicReviewFindingGate(),
            [],
            new DeterministicReviewClaimExtractor(),
            new ReviewContextEvidenceCollector(),
            new SummaryReconciliationService(),
            new DeterministicLocalReviewVerifier());

        var result = await sut.ReviewAsync(CreateJob(), CreatePr(), context, CancellationToken.None, chatClient);

        var comment = Assert.Single(result.Comments);
        Assert.Equal(CommentSeverity.Error, comment.Severity);
    }

    private static ReviewJob CreateJob()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 42, 7);
        job.SelectReviewStrategy(
            ReviewStrategy.PrWideAgentic,
            ReviewStrategySelectionSource.ClientDefault,
            ReviewComparisonMode.Single,
            ReviewPublicationMode.Publish,
            null);
        return job;
    }

    private static PullRequest CreatePr()
    {
        return new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            42,
            7,
            "Refactor service registration",
            "Touches startup, composition root, and tests.",
            "feature/registration",
            "main",
            [
                new ChangedFile("src/Core/Aggregator.cs", ChangeType.Edit, "aggregator", "+return staleAggregate;"),
                new ChangedFile("src/Api/PublishController.cs", ChangeType.Edit, "controller", "+publisher.Publish(result);"),
            ]);
    }

    private static IReviewContextTools CreateReviewTools(bool returnFileContent)
    {
        var reviewTools = Substitute.For<IReviewContextTools>();
        reviewTools.GetChangedFilesAsync(Arg.Any<CancellationToken>())
            .Returns(
            [
                new ChangedFileSummary("src/Core/Aggregator.cs", ChangeType.Edit),
                new ChangedFileSummary("src/Api/PublishController.cs", ChangeType.Edit),
            ]);
        reviewTools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => returnFileContent ? $"content:{call.ArgAt<string>(0)}" : string.Empty);
        reviewTools.GetFileTreeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["src/Core/Aggregator.cs", "src/Api/PublishController.cs"]);
        reviewTools.AskProCursorKnowledgeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ProCursorKnowledgeAnswerDto("no_result", [], "No indexed knowledge."));
        reviewTools.GetProCursorSymbolInfoAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new ProCursorSymbolInsightDto("unavailable", null, false, false, null, []));
        return reviewTools;
    }

    private static IProtocolRecorder CreateProtocolRecorder()
    {
        var recorder = Substitute.For<IProtocolRecorder>();
        recorder.RecordPrWideStageEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        recorder.SetCompletedAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return recorder;
    }

    private sealed class EmptyClaimExtractor : IReviewClaimExtractor
    {
        public IReadOnlyList<ClaimDescriptor> ExtractClaims(CandidateReviewFinding finding)
        {
            return [];
        }
    }
}
