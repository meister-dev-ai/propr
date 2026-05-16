// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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

public sealed class AgenticFileReviewerToolTraceTests
{
    [Fact]
    public async Task ReviewAsync_TriggeredRegistrationFollowUp_UsesNarrowSeedFilesAndBudget()
    {
        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        protocolRecorder.BeginAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
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
        reviewTools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns("anchor content");

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("file summary", []));

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "not-json-plan")),
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
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model", MaxIterations = 3 }),
            Substitute.For<ILogger<AgenticFileByFileReviewOrchestrator>>());

        await sut.ReviewAsync(job, CreateTriggeredFallbackPr(), new ReviewSystemContext(null, [], reviewTools), CancellationToken.None, chatClient);

        await protocolRecorder.Received()
            .RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(),
                ReviewProtocolEventNames.AgenticFilePlanCreated,
                Arg.Any<string?>(),
                Arg.Is<string?>(output => output != null
                                          && output.Contains("\"TriggerFamily\":\"bounded_sibling_context\"", StringComparison.Ordinal)
                                          && output.Contains(
                                              "\"SeedFilePaths\":[\"src/Web/Program.cs\",\"src/Application/Registration.cs\"]", StringComparison.Ordinal)
                                          && output.Contains("\"AllowedTools\":[\"get_file_content\",\"get_file_tree\"]", StringComparison.Ordinal)
                                          && output.Contains("\"MaxToolCalls\":2", StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());

        await protocolRecorder.Received()
            .RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(),
                ReviewProtocolEventNames.AgenticFileInvestigationResult,
                Arg.Is<string?>(details => details != null
                                           && details.Contains("\"taskId\":\"task-001\"", StringComparison.Ordinal)
                                           && details.Contains("\"anchorFile\":\"src/Web/Program.cs\"", StringComparison.Ordinal)),
                Arg.Is<string?>(output => output != null
                                          && output.Contains("\"Status\":\"completed\"", StringComparison.Ordinal)
                                          && output.Contains("\"Degraded\":false", StringComparison.Ordinal)
                                          && output.Contains("\"DiagnosticsOnly\":false", StringComparison.Ordinal)
                                          && output.Contains("\"EvidenceSetId\":\"evidence-task-001\"", StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(BoundedReviewContextTools.BlockedNotAllowedStatus, BoundedReviewContextTools.GetFileTreeToolName, "src/Web/Program.cs", 1)]
    [InlineData(BoundedReviewContextTools.BlockedBudgetExhaustedStatus, BoundedReviewContextTools.GetFileContentToolName, "src/Web/Program.cs", 0)]
    [InlineData(BoundedReviewContextTools.BlockedScopeViolationStatus, BoundedReviewContextTools.GetFileContentToolName, "src/Other.cs", 2)]
    [InlineData("failed", BoundedReviewContextTools.GetFileContentToolName, "src/Web/Program.cs", 2)]
    public async Task ReviewAsync_AgenticInvestigation_UsesAuthoritativeRuntimeTraceStatus(
        string runtimeStatus,
        string requestedTool,
        string requestedPath,
        int maxToolCalls)
    {
        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        protocolRecorder.BeginAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
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
        ConfigureToolBehavior(reviewTools, runtimeStatus);

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
                                                  "max_tool_calls": {{MAX_TOOL_CALLS}}
                                                }
                                              ]
                                            }
                                            """.Replace("{{MAX_TOOL_CALLS}}", maxToolCalls.ToString(), StringComparison.Ordinal))),
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        [CreateFunctionCall(requestedTool, requestedPath)])),
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {
                                              "task_id": "task-001",
                                              "status": "completed",
                                              "evidence": [],
                                              "candidate_findings": [],
                                              "tool_usage": [
                                                { "tool_name": "get_file_content", "status": "success", "target": "fake.cs" }
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

        var options = new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model", MaxIterations = 3 };

        var sut = new AgenticFileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            repository,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(options),
            Substitute.For<ILogger<AgenticFileByFileReviewOrchestrator>>());

        await sut.ReviewAsync(job, CreatePr(), context, CancellationToken.None, chatClient);

        await protocolRecorder.Received()
            .RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(),
                Arg.Is<string>(name => name == ReviewProtocolEventNames.AgenticFileDegraded),
                Arg.Any<string?>(),
                Arg.Is<string?>(output =>
                    output != null &&
                    output.Contains(runtimeStatus, StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());
    }

    private static FunctionCallContent CreateFunctionCall(string requestedTool, string requestedPath)
    {
        return requestedTool == BoundedReviewContextTools.GetFileTreeToolName
            ? new FunctionCallContent(
                "call-1",
                requestedTool,
                new Dictionary<string, object?>
                {
                    ["branch"] = "feature/registration",
                })
            : new FunctionCallContent(
                "call-1",
                requestedTool,
                new Dictionary<string, object?>
                {
                    ["path"] = requestedPath,
                    ["branch"] = "feature/registration",
                    ["startLine"] = 1,
                    ["endLine"] = 50,
                });
    }

    private static void ConfigureToolBehavior(IReviewContextTools reviewTools, string runtimeStatus)
    {
        if (runtimeStatus == "failed")
        {
            reviewTools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromException<string>(new InvalidOperationException("repository fetch failed")));
            return;
        }

        reviewTools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns("anchor content");
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

    private static PullRequest CreateTriggeredFallbackPr()
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
            [
                new ChangedFile("src/Web/Program.cs", ChangeType.Edit, "program content", "+builder.Services.AddFoo();"),
                new ChangedFile("src/Application/Registration.cs", ChangeType.Edit, "registration content", "+services.AddFoo();"),
            ]);
    }
}
