// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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

public sealed class AgenticFileByFileReviewOrchestratorLocalVerificationTests
{
    [Fact]
    public async Task ReviewAsync_AgenticLocalCandidateContradictedByInvariant_IsDroppedBeforePersistence()
    {
        const string planningJson =
            """
            {
              "plan_id": "plan-001",
              "anchor_file_path": "src/Foo.cs",
              "concerns": ["Check nullable review comment claim."],
              "changed_areas": ["src/Foo.cs"],
              "investigation_tasks": [
                {
                  "id": "task-001",
                  "task_type": "concern",
                  "concern": "Check nullable review comment claim.",
                  "seed_file_paths": ["src/Foo.cs"],
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
              "evidence": [],
              "candidate_findings": [
                {
                  "id": "candidate-001",
                  "message": "ReviewComment.Message may be null when the model omits a message.",
                  "severity": "warning",
                  "category": "per_file_comment",
                  "file_path": "src/Foo.cs",
                  "line_number": 10,
                  "supporting_files": ["src/Foo.cs"],
                  "confidence": {
                    "concern": "correctness",
                    "score": 88
                  }
                }
              ],
              "tool_usage": []
            }
            """;

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("file summary", []));

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
        protocolRecorder.RecordVerificationEventAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var job = CreateJob();
        var pr = CreatePr(new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", "diff"));
        var storedResults = new List<ReviewFileResult>();
        var repository = CreateJobRepository(job, storedResults);
        var reviewTools = Substitute.For<IReviewContextTools>();
        reviewTools.GetFileContentAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns("class Foo {}\n");

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, planningJson)),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, investigationJson)),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis summary")));

        var sut = new AgenticFileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            repository,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model" }),
            Substitute.For<ILogger<AgenticFileByFileReviewOrchestrator>>(),
            deterministicReviewFindingGate: new DeterministicReviewFindingGate(),
            reviewInvariantFactProviders:
            [
                new DomainReviewInvariantFactProvider(),
                new PersistenceReviewInvariantFactProvider(),
            ],
            reviewClaimExtractor: new DeterministicReviewClaimExtractor(),
            reviewFindingVerifier: new DeterministicLocalReviewVerifier());

        var result = await sut.ReviewAsync(job, pr, new ReviewSystemContext(null, [], reviewTools), CancellationToken.None);

        var stored = Assert.Single(storedResults);
        Assert.Empty(stored.Comments!);
        Assert.Empty(result.Comments);

        await protocolRecorder.Received()
            .RecordVerificationEventAsync(
                Arg.Any<Guid>(),
                Arg.Is<string>(name => name == ReviewProtocolEventNames.VerificationLocalDecision),
                Arg.Any<string?>(),
                Arg.Is<string?>(output => output != null && output.Contains("\"recommendedDisposition\":\"Drop\"", StringComparison.Ordinal)),
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
}
