// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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

public sealed class AgenticFileByFileReviewOrchestratorFinalGateTests
{
    [Fact]
    public async Task ReviewAsync_UnsupportedAgenticInvestigationCandidate_IsDroppedFromFinalOutput()
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
                    "score": 86
                  }
                }
              ],
              "tool_usage": [
                {
                  "tool_name": "get_file_content",
                  "status": "success",
                  "target": "src/Bar.cs"
                }
              ]
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

        var protocolRecorder = CreateProtocolRecorder();
        var job = CreateJob();
        var pr = CreatePr(new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", "diff"));
        var storedResults = new List<ReviewFileResult>();
        var jobRepo = CreateJobRepository(job, storedResults);

        var reviewTools = Substitute.For<IReviewContextTools>();
        reviewTools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns("services.AddBar();");

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, planningJson)),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, investigationJson)),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, synthesisJson)));

        var sut = new AgenticFileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepo,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model" }),
            Substitute.For<ILogger<AgenticFileByFileReviewOrchestrator>>(),
            deterministicReviewFindingGate: new DeterministicReviewFindingGate(),
            reviewInvariantFactProviders: [new StubInvariantFactProvider([])],
            reviewClaimExtractor: new DeterministicReviewClaimExtractor(),
            reviewFindingVerifier: new DeterministicLocalReviewVerifier());

        var result = await sut.ReviewAsync(job, pr, new ReviewSystemContext(null, [], reviewTools), CancellationToken.None);

        Assert.DoesNotContain(result.Comments, comment => comment.Message == "Missing DI registration in multiple files.");
        Assert.DoesNotContain("Potential DI registration gap spans multiple files.", result.Summary, StringComparison.Ordinal);
        Assert.Contains("No publishable or summary-only findings remained after verification.", result.Summary, StringComparison.Ordinal);

        await protocolRecorder.Received()
            .RecordReviewFindingGateEventAsync(
                Arg.Any<Guid>(),
                Arg.Is<string>(name => name == ReviewProtocolEventNames.ReviewFindingGateDecision),
                Arg.Any<string?>(),
                Arg.Is<string?>(output => output != null
                                          && output.Contains("\"disposition\":\"Drop\"", StringComparison.Ordinal)
                                          && output.Contains("agentic_file_investigation", StringComparison.Ordinal)
                                          && output.Contains("investigation_origin_missing_explicit_support", StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());
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

    private static IProtocolRecorder CreateProtocolRecorder()
    {
        var recorder = Substitute.For<IProtocolRecorder>();
        recorder.BeginAsync(
                Arg.Any<Guid>(),
                Arg.Any<int>(),
                Arg.Any<string?>(),
                Arg.Any<Guid?>(),
                Arg.Any<AiConnectionModelCategory?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Guid.NewGuid()));
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
        recorder.RecordAiCallAsync(
                Arg.Any<Guid>(),
                Arg.Any<int>(),
                Arg.Any<long?>(),
                Arg.Any<long?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>(),
                Arg.Any<string?>())
            .Returns(Task.CompletedTask);
        recorder.RecordReviewFindingGateEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        recorder.RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        recorder.RecordVerificationEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return recorder;
    }

    private sealed class StubInvariantFactProvider(IReadOnlyList<InvariantFact> facts) : IReviewInvariantFactProvider
    {
        public IReadOnlyList<InvariantFact> GetFacts()
        {
            return facts;
        }
    }
}
