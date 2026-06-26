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
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.AgenticFileByFile;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class AgenticFileByFileReviewOrchestratorPrVerificationTests
{
    [Fact]
    public async Task ReviewAsync_AgenticCrossFileCandidate_RunsPrVerificationBeforeFinalGate()
    {
        const string planningJson =
            """
            {
              "plan_id": "plan-001",
              "anchor_file_path": "src/Foo.cs",
              "concerns": ["Check DI registration consistency."],
              "changed_areas": ["src/Foo.cs", "src/Bar.cs"],
              "investigation_tasks": [
                {
                  "id": "task-001",
                  "task_type": "concern",
                  "trigger_family": "dispatch_or_registration",
                  "concern": "Check DI registration consistency.",
                  "seed_file_paths": ["src/Foo.cs", "src/Bar.cs"],
                  "allowed_tools": ["get_file_content"],
                  "max_tool_calls": 1
                }
              ]
            }
            """;

        const string investigationJson =
            """
            {
              "task_id": "task-001",
              "status": "completed",
              "degraded": false,
              "evidence": [
                {
                  "kind": "file_content",
                  "summary": "Captured sibling registration evidence.",
                  "source_id": "src/Bar.cs"
                }
              ],
              "candidate_findings": [
                {
                  "id": "candidate-001",
                  "message": "Missing DI registration in multiple files.",
                  "severity": "warning",
                  "category": "architecture",
                  "file_path": "src/Foo.cs",
                  "line_number": 12,
                  "candidate_summary_text": "Potential DI registration gap spans multiple files.",
                  "supporting_files": ["src/Foo.cs", "src/Bar.cs"],
                  "confidence": {
                    "concern": "correctness",
                    "score": 90
                  }
                }
              ],
              "tool_usage": []
            }
            """;

        const string prVerificationJson =
            """
            {
              "verdict": "supported",
              "recommended_disposition": "Publish",
              "reason_codes": ["verified_bounded_claim_support"],
              "summary": "The retrieved repository evidence directly supports the cross-file claim."
            }
            """;

        const string synthesisJson =
            """
            {
              "summary": "Base summary.",
              "cross_cutting_concerns": []
            }
            """;

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("file summary", []));

        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        protocolRecorder.BeginAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<ReviewPassKind?>(), Arg.Any<string?>())
            .Returns(Guid.NewGuid());
        protocolRecorder.SetCompletedAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordAiCallAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordReviewFindingGateEventAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordVerificationEventAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.AddTokensAsync(
                Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var job = CreateJob();
        var pr = CreatePr(new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", "diff"));
        var storedResults = new List<ReviewFileResult>();
        var repository = CreateJobRepository(job, storedResults);

        var reviewTools = Substitute.For<IReviewContextTools>();
        reviewTools.GetChangedFilesAsync(Arg.Any<CancellationToken>())
            .Returns(
            [
                new ChangedFileSummary("src/Foo.cs", ChangeType.Edit),
                new ChangedFileSummary("src/Bar.cs", ChangeType.Edit),
            ]);
        reviewTools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var path = callInfo.ArgAt<string>(0);
                return path == "src/Bar.cs" ? "services.AddBar();" : "services.AddFoo();";
            });
        reviewTools.AskProCursorKnowledgeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ProCursorKnowledgeAnswerDto("ok", [], "Evidence found."));
        reviewTools.GetProCursorSymbolInfoAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new ProCursorSymbolInsightDto("unavailable", null, false, false, null, []));

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, planningJson)),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, investigationJson)),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, synthesisJson)),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, prVerificationJson))
                {
                    Usage = new UsageDetails { InputTokenCount = 41, OutputTokenCount = 13 },
                });

        var systemContext = new ReviewSystemContext(null, [], reviewTools)
        {
            DefaultReviewChatClient = chatClient,
            DefaultReviewModelId = "test-model",
            ModelId = "test-model",
            Temperature = 0.25f,
        };

        var sut = new AgenticFileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            repository,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model" }),
            Substitute.For<ILogger<AgenticFileByFileReviewOrchestrator>>(),
            deterministicReviewFindingGate: new DeterministicReviewFindingGate(),
            reviewInvariantFactProviders: [new StubInvariantFactProvider([])],
            reviewClaimExtractor: new DeterministicReviewClaimExtractor(),
            reviewFindingVerifier: new DeterministicLocalReviewVerifier(),
            reviewEvidenceCollector: new ReviewContextEvidenceCollector());

        var result = await sut.ReviewAsync(job, pr, systemContext, CancellationToken.None);

        Assert.Contains(result.Comments, comment => comment.Message == "Missing DI registration in multiple files.");

        await protocolRecorder.Received()
            .RecordVerificationEventAsync(
                Arg.Any<Guid>(),
                ReviewProtocolEventNames.VerificationPrDecision,
                Arg.Is<string?>(details => details != null && details.Contains("verifierFamilies", StringComparison.Ordinal)),
                Arg.Is<string?>(output => output != null
                                          && output.Contains("\"recommendedDisposition\":\"Publish\"", StringComparison.Ordinal)
                                          && output.Contains(ReviewFindingGateReasonCodes.VerifiedBoundedClaimSupport, StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());

        await protocolRecorder.Received()
            .RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(),
                ReviewProtocolEventNames.AgenticFileFollowUpDependencyRecorded,
                Arg.Is<string?>(details => details != null
                                           && details.Contains("\"anchorFile\":\"src/Foo.cs\"", StringComparison.Ordinal)
                                           && details.Contains("\"taskId\":\"task-001\"", StringComparison.Ordinal)
                                           && details.Contains("\"triggerFamily\":\"dispatch_or_registration\"", StringComparison.Ordinal)),
                Arg.Is<string?>(output => output != null
                                          && output.Contains("\"dependencyRecorded\":true", StringComparison.Ordinal)
                                          && output.Contains("\"findingId\":\"candidate-001\"", StringComparison.Ordinal)
                                          && output.Contains("\"evidenceSetId\":\"evidence-task-001\"", StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WithPromptExperiment_UsesVariantPromptsAcrossAgenticStagesAndVerification()
    {
        const string planningJson =
            """
            {
              "plan_id": "plan-001",
              "anchor_file_path": "src/Foo.cs",
              "concerns": ["Check DI registration consistency."],
              "changed_areas": ["src/Foo.cs", "src/Bar.cs"],
              "investigation_tasks": [
                {
                  "id": "task-001",
                  "task_type": "concern",
                  "trigger_family": "dispatch_or_registration",
                  "concern": "Check DI registration consistency.",
                  "seed_file_paths": ["src/Foo.cs", "src/Bar.cs"],
                  "allowed_tools": ["get_file_content"],
                  "max_tool_calls": 1
                }
              ]
            }
            """;

        const string investigationJson =
            """
            {
              "task_id": "task-001",
              "status": "completed",
              "degraded": false,
              "evidence": [
                {
                  "kind": "file_content",
                  "summary": "Captured sibling registration evidence.",
                  "source_id": "src/Bar.cs"
                }
              ],
              "candidate_findings": [
                {
                  "id": "candidate-001",
                  "message": "Missing DI registration in multiple files.",
                  "severity": "warning",
                  "category": "architecture",
                  "file_path": "src/Foo.cs",
                  "line_number": 12,
                  "candidate_summary_text": "Potential DI registration gap spans multiple files.",
                  "supporting_files": ["src/Foo.cs", "src/Bar.cs"],
                  "confidence": {
                    "concern": "correctness",
                    "score": 90
                  }
                }
              ],
              "tool_usage": []
            }
            """;

        const string prVerificationJson =
            """
            {
              "verdict": "supported",
              "recommended_disposition": "Publish",
              "reason_codes": ["verified_bounded_claim_support"],
              "summary": "The retrieved repository evidence directly supports the cross-file claim."
            }
            """;

        const string synthesisJson =
            """
            {
              "summary": "Base summary.",
              "cross_cutting_concerns": []
            }
            """;

        ReviewSystemContext? capturedPerFileContext = null;
        var capturedChatCalls = new List<List<(ChatRole Role, string? Text)>>();
        var promptExperiment = new PromptExperimentContext(
            "variant-agentic",
            [
                new StagePromptVariant(
                    PromptStageKeys.AgenticFilePlanningSystem, PromptStageRole.System, PromptCompositionMode.Prepend, "Variant planning system"),
                new StagePromptVariant(PromptStageKeys.AgenticFilePlanningUser, PromptStageRole.User, PromptCompositionMode.Replace, "Variant planning user"),
                new StagePromptVariant(
                    PromptStageKeys.AgenticFileInvestigationSystem, PromptStageRole.System, PromptCompositionMode.Prepend, "Variant investigation system"),
                new StagePromptVariant(
                    PromptStageKeys.AgenticFileInvestigationUser, PromptStageRole.User, PromptCompositionMode.Replace, "Variant investigation user"),
                new StagePromptVariant(
                    PromptStageKeys.PrVerificationSystem, PromptStageRole.System, PromptCompositionMode.Prepend, "Variant verification system"),
                new StagePromptVariant(PromptStageKeys.PrVerificationUser, PromptStageRole.User, PromptCompositionMode.Replace, "Variant verification user"),
            ]);

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(
                Arg.Any<PullRequest>(),
                Arg.Do<ReviewSystemContext>(context => capturedPerFileContext = context),
                Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("file summary", []));

        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        protocolRecorder.BeginAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<ReviewPassKind?>(), Arg.Any<string?>())
            .Returns(Guid.NewGuid());
        protocolRecorder.SetCompletedAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordAiCallAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<long?>(), Arg.Any<long?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordReviewFindingGateEventAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordVerificationEventAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.AddTokensAsync(
                Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordToolCallAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var job = CreateJob();
        var pr = CreatePr(new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", "diff"));
        var storedResults = new List<ReviewFileResult>();
        var repository = CreateJobRepository(job, storedResults);

        var reviewTools = Substitute.For<IReviewContextTools>();
        reviewTools.GetChangedFilesAsync(Arg.Any<CancellationToken>())
            .Returns(
            [
                new ChangedFileSummary("src/Foo.cs", ChangeType.Edit),
                new ChangedFileSummary("src/Bar.cs", ChangeType.Edit),
            ]);
        reviewTools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var path = callInfo.ArgAt<string>(0);
                return path == "src/Bar.cs" ? "services.AddBar();" : "services.AddFoo();";
            });
        reviewTools.AskProCursorKnowledgeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ProCursorKnowledgeAnswerDto("ok", [], "Evidence found."));
        reviewTools.GetProCursorSymbolInfoAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new ProCursorSymbolInsightDto("unavailable", null, false, false, null, []));

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var messages = callInfo.Arg<IList<ChatMessage>>();
                capturedChatCalls.Add(messages.Select(message => (message.Role, (string?)message.Text)).ToList());
                return Task.FromResult(
                    capturedChatCalls.Count switch
                    {
                        1 => new ChatResponse(new ChatMessage(ChatRole.Assistant, planningJson)),
                        2 => new ChatResponse(new ChatMessage(ChatRole.Assistant, investigationJson)),
                        3 => new ChatResponse(new ChatMessage(ChatRole.Assistant, synthesisJson)),
                        4 => new ChatResponse(new ChatMessage(ChatRole.Assistant, prVerificationJson))
                        {
                            Usage = new UsageDetails { InputTokenCount = 41, OutputTokenCount = 13 },
                        },
                        _ => throw new InvalidOperationException("Unexpected chat invocation."),
                    });
            });

        var systemContext = new ReviewSystemContext(null, [], reviewTools)
        {
            DefaultReviewChatClient = chatClient,
            DefaultReviewModelId = "test-model",
            ModelId = "test-model",
            Temperature = 0.25f,
            PromptExperiment = promptExperiment,
        };

        var sut = new AgenticFileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            repository,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model" }),
            Substitute.For<ILogger<AgenticFileByFileReviewOrchestrator>>(),
            deterministicReviewFindingGate: new DeterministicReviewFindingGate(),
            reviewInvariantFactProviders: [new StubInvariantFactProvider([])],
            reviewClaimExtractor: new DeterministicReviewClaimExtractor(),
            reviewFindingVerifier: new DeterministicLocalReviewVerifier(),
            reviewEvidenceCollector: new ReviewContextEvidenceCollector());

        var result = await sut.ReviewAsync(job, pr, systemContext, CancellationToken.None);

        Assert.Contains(result.Comments, comment => comment.Message == "Missing DI registration in multiple files.");
        Assert.NotNull(capturedPerFileContext);
        Assert.Same(promptExperiment, capturedPerFileContext!.PromptExperiment);
        Assert.Equal(4, capturedChatCalls.Count);
        Assert.Contains(
            capturedChatCalls[0],
            message => message.Role == ChatRole.System && message.Text != null && message.Text.StartsWith("Variant planning system", StringComparison.Ordinal));
        Assert.Contains(capturedChatCalls[0], message => message.Role == ChatRole.User && message.Text == "Variant planning user");
        Assert.Contains(
            capturedChatCalls[1],
            message => message.Role == ChatRole.System && message.Text != null &&
                       message.Text.StartsWith("Variant investigation system", StringComparison.Ordinal));
        Assert.Contains(capturedChatCalls[1], message => message.Role == ChatRole.User && message.Text == "Variant investigation user");
        Assert.Contains(
            capturedChatCalls[3],
            message => message.Role == ChatRole.System && message.Text != null &&
                       message.Text.StartsWith("Variant verification system", StringComparison.Ordinal));
        Assert.Contains(capturedChatCalls[3], message => message.Role == ChatRole.User && message.Text == "Variant verification user");
    }

    private static ReviewJob CreateJob()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
        job.SelectReviewStrategy(
            ReviewStrategy.AgenticFileByFile,
            ReviewStrategySelectionSource.ClientDefault,
            ReviewComparisonMode.Single,
            ReviewPublicationMode.Publish,
            null);
        return job;
    }

    private static PullRequest CreatePr(params ChangedFile[] files)
    {
        return new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            1,
            "Test PR",
            null,
            "feature/x",
            "main",
            files.ToList().AsReadOnly());
    }

    private static IJobRepository CreateJobRepository(ReviewJob job, List<ReviewFileResult> storedResults)
    {
        var repository = Substitute.For<IJobRepository>();
        repository.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repository.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                storedResults.Add(callInfo.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repository.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return repository;
    }

    private static ReviewJob BuildJobWithResults(ReviewJob original, IEnumerable<ReviewFileResult> results)
    {
        var job = new ReviewJob(
            original.Id,
            original.ClientId,
            original.OrganizationUrl,
            original.ProjectId,
            original.RepositoryId,
            original.PullRequestId,
            original.IterationId);

        foreach (var result in results)
        {
            job.FileReviewResults.Add(result);
        }

        return job;
    }

    private sealed class StubInvariantFactProvider(IReadOnlyList<InvariantFact> facts) : IReviewInvariantFactProvider
    {
        public IReadOnlyList<InvariantFact> GetFacts()
        {
            return facts;
        }
    }
}
