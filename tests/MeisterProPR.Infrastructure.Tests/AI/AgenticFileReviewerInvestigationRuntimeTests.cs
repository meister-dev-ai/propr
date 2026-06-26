// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies.AgenticFileByFile;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class AgenticFileReviewerInvestigationRuntimeTests
{
    [Fact]
    public async Task ReviewAsync_AgenticInvestigation_UsesRuntimeToolLoopAndAuthoritativeToolAttempts()
    {
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
        protocolRecorder.RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordToolCallAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var repository = Substitute.For<IJobRepository>();
        var storedResults = new List<ReviewFileResult>();
        var job = CreateJob();
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

        var reviewTools = Substitute.For<IReviewContextTools>();
        reviewTools.GetFileContentAsync("src/Web/Program.cs", Arg.Any<string>(), 1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns("builder.Services.AddFoo();");

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("file summary", []));

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {
                                              "plan_id": "plan-001",
                                              "anchor_file_path": "src/Web/Program.cs",
                                              "concerns": ["Check registration wiring."],
                                              "changed_areas": ["src/Web/Program.cs"],
                                              "investigation_tasks": [
                                                {
                                                  "id": "task-001",
                                                  "task_type": "concern",
                                                  "concern": "Check registration wiring.",
                                                  "seed_file_paths": ["src/Web/Program.cs"],
                                                  "allowed_tools": ["get_file_content"],
                                                  "max_tool_calls": 2
                                                }
                                              ]
                                            }
                                            """)),
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        [
                            new FunctionCallContent(
                                "call-1", "get_file_content", new Dictionary<string, object?>
                                {
                                    ["path"] = "src/Web/Program.cs",
                                    ["branch"] = "feature/registration",
                                    ["startLine"] = 1,
                                    ["endLine"] = 50,
                                }),
                        ])),
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {
                                              "task_id": "task-001",
                                              "status": "completed",
                                              "evidence": [
                                                { "kind": "file_content", "summary": "Verified runtime registration lookup.", "source_id": "src/Web/Program.cs" }
                                              ],
                                              "candidate_findings": [],
                                              "tool_usage": [
                                                { "tool_name": "get_file_content", "status": "blocked_scope_violation", "target": "hallucinated.cs" }
                                              ],
                                              "degraded": false
                                            }
                                            """)),
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {
                                              "summary": "fallback summary",
                                              "cross_cutting_concerns": []
                                            }
                                            """)));

        var context = new ReviewSystemContext(null, [], reviewTools)
        {
            ActiveProtocolId = Guid.NewGuid(),
            ProtocolRecorder = protocolRecorder,
        };

        var sut = new AgenticFileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            repository,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model", MaxIterations = 4 }),
            Substitute.For<ILogger<AgenticFileByFileReviewOrchestrator>>());

        await sut.ReviewAsync(job, CreatePr(), context, CancellationToken.None, chatClient);

        await reviewTools.Received(2)
            .GetFileContentAsync("src/Web/Program.cs", "feature/registration", 1, Arg.Any<int>(), Arg.Any<CancellationToken>());

        await protocolRecorder.Received(1)
            .RecordToolCallAsync(
                Arg.Any<Guid>(),
                BoundedReviewContextTools.GetFileContentToolName,
                Arg.Is<string>(args => args.Contains("src/Web/Program.cs", StringComparison.Ordinal)),
                Arg.Is<string>(result => result.Contains("builder.Services.AddFoo();", StringComparison.Ordinal)),
                1,
                Arg.Any<CancellationToken>());

        await protocolRecorder.DidNotReceive()
            .RecordToolCallAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Is<string>(args => args.Contains("hallucinated.cs", StringComparison.Ordinal)),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>());

        await protocolRecorder.Received()
            .RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(),
                ReviewProtocolEventNames.AgenticFileInvestigationResult,
                Arg.Any<string?>(),
                Arg.Is<string?>(output =>
                    output != null &&
                    output.Contains("\"Status\":\"completed\"", StringComparison.Ordinal) &&
                    output.Contains("\"ToolName\":\"get_file_content\"", StringComparison.Ordinal) &&
                    output.Contains("\"success\"", StringComparison.Ordinal) &&
                    !output.Contains("blocked_scope_violation", StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_ParseFailedInvestigation_RemainsDiagnosticsOnlyAndPublishesNoCandidateFinding()
    {
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
        protocolRecorder.RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordToolCallAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var repository = Substitute.For<IJobRepository>();
        var storedResults = new List<ReviewFileResult>();
        var job = CreateJob();
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

        var reviewTools = Substitute.For<IReviewContextTools>();
        reviewTools.GetFileContentAsync("src/Web/Program.cs", Arg.Any<string>(), 1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns("builder.Services.AddFoo();");

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("file summary", []));

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {
                                              "plan_id": "plan-001",
                                              "anchor_file_path": "src/Web/Program.cs",
                                              "concerns": ["Check registration wiring."],
                                              "changed_areas": ["src/Web/Program.cs"],
                                              "investigation_tasks": [
                                                {
                                                  "id": "task-001",
                                                  "task_type": "concern",
                                                  "concern": "Check registration wiring.",
                                                  "seed_file_paths": ["src/Web/Program.cs"],
                                                  "allowed_tools": ["get_file_content"],
                                                  "max_tool_calls": 2
                                                }
                                              ]
                                            }
                                            """)),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "this is not valid investigation json")),
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {
                                              "summary": "fallback summary",
                                              "cross_cutting_concerns": []
                                            }
                                            """)));

        var context = new ReviewSystemContext(null, [], reviewTools)
        {
            ActiveProtocolId = Guid.NewGuid(),
            ProtocolRecorder = protocolRecorder,
        };

        var sut = new AgenticFileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            repository,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model", MaxIterations = 4 }),
            Substitute.For<ILogger<AgenticFileByFileReviewOrchestrator>>());

        var result = await sut.ReviewAsync(job, CreatePr(), context, CancellationToken.None, chatClient);

        Assert.Empty(result.Comments);
        Assert.DoesNotContain("registration wiring", result.Summary, StringComparison.OrdinalIgnoreCase);

        await protocolRecorder.Received()
            .RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(),
                ReviewProtocolEventNames.AgenticFileDegraded,
                Arg.Any<string?>(),
                Arg.Is<string?>(output => output != null
                                          && output.Contains("\"DiagnosticsOnly\":true", StringComparison.Ordinal)
                                          && output.Contains("\"candidateCount\":0", StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_StraightforwardSingleFile_SkipsStageBAndDoesNotLaunchInvestigation()
    {
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
        protocolRecorder.RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var repository = Substitute.For<IJobRepository>();
        var storedResults = new List<ReviewFileResult>();
        var job = CreateJob();
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

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("file summary", []));

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {
                                              "plan_id": "plan-001",
                                              "anchor_file_path": "src/Foo.cs",
                                              "concerns": ["Check behavioral impact around src/Foo.cs."],
                                              "changed_areas": ["src/Foo.cs"],
                                              "investigation_tasks": [],
                                              "no_investigation_reason": "No explicit trigger family required deeper follow-up for this straightforward file."
                                            }
                                            """)),
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {
                                              "summary": "fallback summary",
                                              "cross_cutting_concerns": []
                                            }
                                            """)));

        var sut = new AgenticFileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            repository,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model", MaxIterations = 4 }),
            Substitute.For<ILogger<AgenticFileByFileReviewOrchestrator>>());

        await sut.ReviewAsync(job, CreateStraightforwardSingleFilePr(), new ReviewSystemContext(null, [], null), CancellationToken.None, chatClient);

        await protocolRecorder.DidNotReceive()
            .RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(),
                ReviewProtocolEventNames.AgenticFileInvestigationLaunched,
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>());
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

        job.SelectReviewStrategy(
            original.ReviewStrategy,
            original.ReviewStrategySelectionSource,
            original.ReviewComparisonMode,
            original.ReviewPublicationMode,
            original.ComparisonGroupId);

        foreach (var result in results)
        {
            job.FileReviewResults.Add(result);
        }

        return job;
    }

    private static ReviewJob CreateJob()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 42, 7);
        job.SelectReviewStrategy(
            ReviewStrategy.AgenticFileByFile,
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
            "Touches startup and composition root.",
            "feature/registration",
            "main",
            [new ChangedFile("src/Web/Program.cs", ChangeType.Edit, "program content", "+builder.Services.AddFoo();")]);
    }

    private static PullRequest CreateStraightforwardSingleFilePr()
    {
        return new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            42,
            7,
            "Small formatting cleanup",
            "Touches one straightforward file.",
            "feature/cleanup",
            "main",
            [new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", "+return value?.Trim();")]);
    }
}
