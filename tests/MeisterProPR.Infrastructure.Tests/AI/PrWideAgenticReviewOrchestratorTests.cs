// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs.ProCursor;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.PrWideAgentic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class PrWideAgenticReviewOrchestratorTests
{
    [Fact]
    public async Task ReviewAsync_CreatesPlanRunsBoundedInvestigationsAndRecordsStageEvents()
    {
        var fallback = Substitute.For<IFileByFileReviewOrchestrator>();
        fallback.ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(new ReviewResult("fallback summary", []));

        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        protocolRecorder.RecordPrWideStageEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var reviewTools = Substitute.For<IReviewContextTools>();
        reviewTools.GetChangedFilesAsync(Arg.Any<CancellationToken>())
            .Returns(
            [
                new ChangedFileSummary("src/Web/Program.cs", ChangeType.Edit),
                new ChangedFileSummary("src/Application/Registration.cs", ChangeType.Edit),
                new ChangedFileSummary("tests/RegistrationTests.cs", ChangeType.Edit),
            ]);
        reviewTools.GetFileTreeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["src/Web/Program.cs", "src/Application/Registration.cs", "tests/RegistrationTests.cs"]);
        reviewTools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => $"content:{call.ArgAt<string>(0)}");
        reviewTools.AskProCursorKnowledgeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ProCursorKnowledgeAnswerDto("no_result", [], "No indexed knowledge."));
        reviewTools.GetProCursorSymbolInfoAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new ProCursorSymbolInsightDto("unavailable", null, false, false, null, []));

        var context = new ReviewSystemContext(null, [], reviewTools)
        {
            ActiveProtocolId = Guid.NewGuid(),
            ProtocolRecorder = protocolRecorder,
        };

        var sut = new PrWideAgenticReviewOrchestrator(
            fallback,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()),
            Substitute.For<ILogger<PrWideAgenticReviewOrchestrator>>());

        var result = await sut.ReviewAsync(
            CreateJob(),
            CreatePr(),
            context,
            CancellationToken.None,
            Substitute.For<IChatClient>());

        Assert.Equal("fallback summary", result.Summary);
        await fallback.Received(1).ReviewAsync(
            Arg.Any<ReviewJob>(),
            Arg.Any<PullRequest>(),
            Arg.Any<ReviewSystemContext>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<IChatClient?>());

        await protocolRecorder.Received(1).RecordPrWideStageEventAsync(
            context.ActiveProtocolId.Value,
            Arg.Is<string>(eventName => eventName == "pr_wide_plan_created"),
            Arg.Is<string?>(details => details != null && details.Contains("planning", StringComparison.Ordinal)),
            Arg.Is<string?>(output => output != null && output.Contains("investigationTasks", StringComparison.Ordinal)),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());

        await protocolRecorder.Received(2).RecordPrWideStageEventAsync(
            context.ActiveProtocolId.Value,
            Arg.Is<string>(eventName => eventName == "pr_wide_investigation_launched"),
            Arg.Any<string>(),
            Arg.Is<string?>(output => output != null && output.Contains("allowedTools", StringComparison.Ordinal)),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());

        await protocolRecorder.Received(2).RecordPrWideStageEventAsync(
            context.ActiveProtocolId.Value,
            Arg.Is<string>(eventName => eventName == "pr_wide_investigation_result"),
            Arg.Any<string>(),
            Arg.Is<string?>(output => output != null && output.Contains("toolUsage", StringComparison.Ordinal)),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());

        await protocolRecorder.Received(1).RecordPrWideStageEventAsync(
            context.ActiveProtocolId.Value,
            Arg.Is<string>(eventName => eventName == "pr_wide_synthesis_completed"),
            Arg.Any<string>(),
            Arg.Is<string?>(output => output != null && output.Contains("candidateCount", StringComparison.Ordinal)),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());

        await reviewTools.Received(2)
            .GetFileContentAsync(Arg.Any<string>(), "feature/registration", Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await reviewTools.DidNotReceive()
            .AskProCursorKnowledgeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WithTooManyChangedFiles_PlansFocusedInvestigationsInsteadOfAllFiles()
    {
        var fallback = Substitute.For<IFileByFileReviewOrchestrator>();
        fallback.ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(new ReviewResult("fallback summary", []));

        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        protocolRecorder.RecordPrWideStageEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var reviewTools = Substitute.For<IReviewContextTools>();
        reviewTools.GetChangedFilesAsync(Arg.Any<CancellationToken>())
            .Returns(
                Enumerable.Range(1, 10)
                    .Select(index => new ChangedFileSummary($"src/File{index}.cs", ChangeType.Edit))
                    .ToArray());
        reviewTools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);
        reviewTools.GetFileTreeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);
        reviewTools.AskProCursorKnowledgeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ProCursorKnowledgeAnswerDto("no_result", [], "No indexed knowledge."));
        reviewTools.GetProCursorSymbolInfoAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new ProCursorSymbolInsightDto("unavailable", null, false, false, null, []));

        var context = new ReviewSystemContext(null, [], reviewTools)
        {
            ActiveProtocolId = Guid.NewGuid(),
            ProtocolRecorder = protocolRecorder,
        };

        var sut = new PrWideAgenticReviewOrchestrator(
            fallback,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()),
            Substitute.For<ILogger<PrWideAgenticReviewOrchestrator>>());

        await sut.ReviewAsync(
            CreateJob(),
            CreateLargePr(),
            context,
            CancellationToken.None,
            Substitute.For<IChatClient>());

        await protocolRecorder.Received(1).RecordPrWideStageEventAsync(
            context.ActiveProtocolId.Value,
            Arg.Is<string>(eventName => eventName == "pr_wide_plan_created"),
            Arg.Any<string>(),
            Arg.Is<string?>(output =>
                output != null &&
                output.Contains("task-001", StringComparison.Ordinal) &&
                output.Contains("task-002", StringComparison.Ordinal) &&
                !output.Contains("task-003", StringComparison.Ordinal)),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());

        await protocolRecorder.Received(2).RecordPrWideStageEventAsync(
            context.ActiveProtocolId.Value,
            Arg.Is<string>(eventName => eventName == "pr_wide_investigation_launched"),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WithoutActiveProtocolContext_CreatesAndCompletesPrWideProtocolPass()
    {
        var fallback = Substitute.For<IFileByFileReviewOrchestrator>();
        fallback.ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(new ReviewResult("fallback summary", []));

        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        var prWideProtocolId = Guid.NewGuid();
        protocolRecorder.BeginAsync(
                Arg.Any<Guid>(),
                Arg.Any<int>(),
                Arg.Any<string?>(),
                Arg.Any<Guid?>(),
                Arg.Any<AiConnectionModelCategory?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<ReviewPassKind?>(), Arg.Any<string?>())
            .Returns(prWideProtocolId);
        protocolRecorder.RecordPrWideStageEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var reviewTools = Substitute.For<IReviewContextTools>();
        reviewTools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => $"content:{call.ArgAt<string>(0)}");

        var context = new ReviewSystemContext(null, [], reviewTools)
        {
            ProtocolRecorder = protocolRecorder,
        };

        var sut = new PrWideAgenticReviewOrchestrator(
            fallback,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()),
            Substitute.For<ILogger<PrWideAgenticReviewOrchestrator>>());

        await sut.ReviewAsync(
            CreateJob(),
            CreatePr(),
            context,
            CancellationToken.None,
            Substitute.For<IChatClient>());

        await protocolRecorder.Received(1).BeginAsync(
            Arg.Any<Guid>(),
            Arg.Any<int>(),
            Arg.Is<string?>(label => label == "pr-wide-review"),
            Arg.Is<Guid?>(fileResultId => fileResultId == null),
            Arg.Is<AiConnectionModelCategory?>(category => category == AiConnectionModelCategory.HighEffort),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

        await protocolRecorder.Received(1).SetCompletedAsync(
            prWideProtocolId,
            Arg.Is<string>(outcome => outcome == "Completed"),
            0,
            0,
            0,
            2,
            null,
            Arg.Any<CancellationToken>());

        await protocolRecorder.Received().RecordPrWideStageEventAsync(
            prWideProtocolId,
            Arg.Is<string>(eventName => eventName == ReviewProtocolEventNames.PrWidePlanCreated),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WithChatClient_ParsesPlannerArtifactsIntoStageEvents()
    {
        var fallback = Substitute.For<IFileByFileReviewOrchestrator>();
        fallback.ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(new ReviewResult("fallback summary", []));

        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        protocolRecorder.RecordPrWideStageEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var reviewTools = Substitute.For<IReviewContextTools>();
        reviewTools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(string.Empty);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {
                                              "plan_id": "plan-ai-001",
                                              "concerns": ["Check dependency registration changes."],
                                              "changed_areas": ["src/Web", "tests"],
                                              "investigation_tasks": [
                                                {
                                                  "id": "task-ai-001",
                                                  "task_type": "concern",
                                                  "concern": "Check dependency registration changes.",
                                                  "seed_file_paths": ["src/Web/Program.cs"],
                                                  "allowed_tools": ["get_file_content", "get_file_tree"],
                                                  "max_tool_calls": 1
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
            Substitute.For<ILogger<PrWideAgenticReviewOrchestrator>>());

        await sut.ReviewAsync(
            CreateJob(),
            CreatePr(),
            context,
            CancellationToken.None,
            chatClient);

        await protocolRecorder.Received(1).RecordPrWideStageEventAsync(
            context.ActiveProtocolId.Value,
            Arg.Is<string>(eventName => eventName == ReviewProtocolEventNames.PrWidePlanCreated),
            Arg.Any<string?>(),
            Arg.Is<string?>(output =>
                output != null &&
                output.Contains("plan-ai-001", StringComparison.Ordinal) &&
                output.Contains("task-ai-001", StringComparison.Ordinal)),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WithOwnedProtocolPass_RecordsAiUsageAndCompletesWithAggregatedTotals()
    {
        var fallback = Substitute.For<IFileByFileReviewOrchestrator>();
        fallback.ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(new ReviewResult("fallback summary", []));

        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        var prWideProtocolId = Guid.NewGuid();
        protocolRecorder.BeginAsync(
                Arg.Any<Guid>(),
                Arg.Any<int>(),
                Arg.Any<string?>(),
                Arg.Any<Guid?>(),
                Arg.Any<AiConnectionModelCategory?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<ReviewPassKind?>(), Arg.Any<string?>())
            .Returns(prWideProtocolId);
        protocolRecorder.RecordPrWideStageEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var reviewTools = Substitute.For<IReviewContextTools>();
        reviewTools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => $"content:{call.ArgAt<string>(0)}");

        var chatClient = Substitute.For<IChatClient>();
        var planningResponse = new ChatResponse(
            new ChatMessage(
                ChatRole.Assistant, """
                                    {
                                      "plan_id": "plan-ai-001",
                                      "concerns": ["Check dependency registration changes."],
                                      "changed_areas": ["src/Web"],
                                      "investigation_tasks": [
                                        {
                                          "id": "task-ai-001",
                                          "task_type": "concern",
                                          "concern": "Check dependency registration changes.",
                                          "seed_file_paths": ["src/Web/Program.cs"],
                                          "allowed_tools": ["get_file_content"],
                                          "max_tool_calls": 1
                                        }
                                      ]
                                    }
                                    """))
        {
            Usage = new UsageDetails { InputTokenCount = 11, OutputTokenCount = 3 },
        };
        var investigationResponse = new ChatResponse(
            new ChatMessage(
                ChatRole.Assistant, """
                                    {
                                      "task_id": "task-ai-001",
                                      "status": "completed",
                                      "evidence": [{ "kind": "file_content", "summary": "Captured content", "source_id": "src/Web/Program.cs" }],
                                      "candidate_findings": [
                                        {
                                          "id": "candidate-task-ai-001",
                                          "message": "Dependency registration might be incomplete.",
                                          "category": "cross_cutting",
                                          "confidence": { "concern": "registration", "score": 82 },
                                          "evidence_reference": { "excerpts": [], "supporting_files": ["src/Web/Program.cs"], "resolution_state": "resolved", "retrieval_mode": "pr_wide_investigation" },
                                          "related_file_paths": ["src/Web/Program.cs"]
                                        }
                                      ],
                                      "tool_usage": [{ "tool_name": "get_file_content", "status": "success", "target": "src/Web/Program.cs" }],
                                      "degraded": false
                                    }
                                    """))
        {
            Usage = new UsageDetails { InputTokenCount = 13, OutputTokenCount = 5 },
        };
        var synthesisResponse = new ChatResponse(
            new ChatMessage(
                ChatRole.Assistant, """
                                    {
                                      "summary": "PR-wide synthesis produced one candidate finding.",
                                      "candidate_findings": [
                                        {
                                          "id": "candidate-task-ai-001",
                                          "message": "Dependency registration might be incomplete.",
                                          "category": "cross_cutting",
                                          "confidence": { "concern": "registration", "score": 88 },
                                          "evidence_reference": { "excerpts": [], "supporting_files": ["src/Web/Program.cs"], "resolution_state": "resolved", "retrieval_mode": "pr_wide_synthesis" },
                                          "related_file_paths": ["src/Web/Program.cs"]
                                        }
                                      ]
                                    }
                                    """))
        {
            Usage = new UsageDetails { InputTokenCount = 17, OutputTokenCount = 7 },
        };
        var callCount = 0;
        chatClient.GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;

                var response = callCount switch
                {
                    1 => planningResponse,
                    2 => investigationResponse,
                    _ => synthesisResponse,
                };

                return Task.FromResult(response);
            });

        var context = new ReviewSystemContext(null, [], reviewTools)
        {
            ProtocolRecorder = protocolRecorder,
            ModelId = "test-model",
        };

        var sut = new PrWideAgenticReviewOrchestrator(
            fallback,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()),
            Substitute.For<ILogger<PrWideAgenticReviewOrchestrator>>());

        await sut.ReviewAsync(
            CreateJob(),
            CreatePr(),
            context,
            CancellationToken.None,
            chatClient);

        await protocolRecorder.Received(3).RecordAiCallAsync(
            prWideProtocolId,
            1,
            Arg.Any<long?>(),
            Arg.Any<long?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>(),
            Arg.Any<string?>());

        await protocolRecorder.Received(1).SetCompletedAsync(
            prWideProtocolId,
            "Completed",
            41,
            15,
            3,
            1,
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_RecordsDelegatedStageDEventsBeforeCompletingOwnedPrWideProtocolPass()
    {
        var fallback = Substitute.For<IFileByFileReviewOrchestrator>();
        fallback.ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(
                new ReviewResult(
                    "fallback summary",
                    [
                        new ReviewComment("src/Web/Program.cs", 12, CommentSeverity.Warning, "Inline issue."),
                        new ReviewComment(null, null, CommentSeverity.Error, "PR-level issue."),
                    ]));

        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        var prWideProtocolId = Guid.NewGuid();
        protocolRecorder.BeginAsync(
                Arg.Any<Guid>(),
                Arg.Any<int>(),
                Arg.Any<string?>(),
                Arg.Any<Guid?>(),
                Arg.Any<AiConnectionModelCategory?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(), Arg.Any<ReviewPassKind?>(), Arg.Any<string?>())
            .Returns(prWideProtocolId);
        protocolRecorder.RecordPrWideStageEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var reviewTools = Substitute.For<IReviewContextTools>();
        reviewTools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => $"content:{call.ArgAt<string>(0)}");

        var context = new ReviewSystemContext(null, [], reviewTools)
        {
            ProtocolRecorder = protocolRecorder,
        };

        var sut = new PrWideAgenticReviewOrchestrator(
            fallback,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()),
            Substitute.For<ILogger<PrWideAgenticReviewOrchestrator>>());

        await sut.ReviewAsync(
            CreateJob(),
            CreatePr(),
            context,
            CancellationToken.None,
            Substitute.For<IChatClient>());

        Received.InOrder(() =>
        {
            fallback.ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>());

            protocolRecorder.RecordPrWideStageEventAsync(
                prWideProtocolId,
                ReviewProtocolEventNames.PrWideVerificationCompleted,
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());

            protocolRecorder.RecordPrWideStageEventAsync(
                prWideProtocolId,
                ReviewProtocolEventNames.PrWideFinalGateDecision,
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());

            protocolRecorder.RecordPrWideStageEventAsync(
                prWideProtocolId,
                ReviewProtocolEventNames.PrWideSummaryReconciled,
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());

            protocolRecorder.RecordPrWideStageEventAsync(
                prWideProtocolId,
                ReviewProtocolEventNames.PrWidePublicationPrepared,
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());
        });

        await protocolRecorder.Received(1).RecordPrWideStageEventAsync(
            prWideProtocolId,
            ReviewProtocolEventNames.PrWidePublicationPrepared,
            Arg.Any<string?>(),
            Arg.Is<string?>(output =>
                output != null &&
                output.Contains("\"publishableCommentCount\":2", StringComparison.Ordinal) &&
                output.Contains("\"inlineCommentCount\":1", StringComparison.Ordinal) &&
                output.Contains("\"prLevelCommentCount\":1", StringComparison.Ordinal)),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());
        await protocolRecorder.Received(1).SetCompletedAsync(
            prWideProtocolId,
            "Completed",
            Arg.Any<long>(),
            Arg.Any<long>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Is<int?>(finalConfidence => finalConfidence == null),
            Arg.Any<CancellationToken>());
    }

    private static ReviewJob CreateJob()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 42, 7);
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
                new ChangedFile("src/Web/Program.cs", ChangeType.Edit, "program content", "+builder.Services.AddFoo();"),
                new ChangedFile("src/Application/Registration.cs", ChangeType.Edit, "registration content", "+services.AddFoo();"),
                new ChangedFile("tests/RegistrationTests.cs", ChangeType.Edit, "test content", "+Assert.NotNull(foo);"),
            ]);
    }

    private static PullRequest CreateLargePr()
    {
        var files = Enumerable.Range(1, 10)
            .Select(index => new ChangedFile(
                $"src/File{index}.cs",
                ChangeType.Edit,
                $"content {index}",
                index <= 2 ? $"+critical change {index}" : "+minor cleanup"))
            .ToArray();

        return new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            42,
            7,
            "Wide refactor",
            null,
            "feature/wide-refactor",
            "main",
            files);
    }
}
