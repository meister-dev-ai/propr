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
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class AgenticReviewSynthesisExecutorSummaryGroundingTests
{
    [Fact]
    public async Task ReviewAsync_FinalSummaryIsGroundedToFinalGatedFindingsOnly()
    {
        const string planningJson =
            """
            {
              "plan_id": "plan-001",
              "anchor_file_path": "src/Foo.cs",
              "concerns": ["Check anchor file."],
              "changed_areas": ["src/Foo.cs"],
              "investigation_tasks": [],
              "no_investigation_reason": "No sibling-file investigation required for this anchor file."
            }
            """;

        const string synthesisJson =
            """
            {
              "summary": "The PR definitely has a missing DI registration across the pipeline.",
              "cross_cutting_concerns": []
            }
            """;

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewResult(
                    "file summary",
                    [new ReviewComment("src/Foo.cs", 12, CommentSeverity.Warning, "Confirmed null dereference in ExecuteAsync.")]));

        var protocolRecorder = CreateProtocolRecorder();
        var job = CreateJob();
        var pr = CreatePr();
        var storedResults = new List<ReviewFileResult>();
        var jobRepo = CreateJobRepository(job, storedResults);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, planningJson)),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, synthesisJson)));

        var sut = new AgenticFileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepo,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model" }),
            Substitute.For<ILogger<AgenticFileByFileReviewOrchestrator>>(),
            deterministicReviewFindingGate: new DeterministicReviewFindingGate());

        var result = await sut.ReviewAsync(job, pr, new ReviewSystemContext(null, [], null), CancellationToken.None);

        Assert.DoesNotContain("missing DI registration", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 publishable finding", result.Summary, StringComparison.Ordinal);
        await protocolRecorder.Received()
            .RecordVerificationEventAsync(
                Arg.Any<Guid>(),
                ReviewProtocolEventNames.SummaryReconciliation,
                Arg.Any<string?>(),
                Arg.Is<string?>(output =>
                    output != null && output.Contains("originalSummary", StringComparison.Ordinal) && output.Contains(
                        "deterministic_summary_grounding", StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_GroundingPreservesSafeOverviewNarrative()
    {
        const string planningJson =
            """
            {
              "plan_id": "plan-001",
              "anchor_file_path": "src/Foo.cs",
              "concerns": ["Check anchor file."],
              "changed_areas": ["src/Foo.cs"],
              "investigation_tasks": [],
              "no_investigation_reason": "No sibling-file investigation required for this anchor file."
            }
            """;

        const string synthesisJson =
            """
            {
              "summary": "This PR advances the reconsideration and synthesis flow, but the review surfaced several important correctness and observability issues that should be addressed before relying on the new behavior. The most significant backend concern is protocol and token accounting regressions in the reconsideration path.",
              "cross_cutting_concerns": [
                {
                  "message": "Potential synthesis observability concern.",
                  "severity": "warning",
                  "category": "cross_cutting",
                  "candidateSummaryText": "Synthesis response handling currently reduces observability and is too brittle around malformed JSON, making repair/fallback behavior harder to diagnose.",
                  "supportingFindingIds": ["finding-pf-001"],
                  "supportingFiles": ["src/Foo.cs"],
                  "evidenceResolutionState": "resolved",
                  "evidenceSource": "synthesis_payload"
                }
              ]
            }
            """;

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewResult(
                    "file summary",
                    [new ReviewComment("src/Foo.cs", 12, CommentSeverity.Warning, "Confirmed null dereference in ExecuteAsync.")]));

        var protocolRecorder = CreateProtocolRecorder();
        var job = CreateJob();
        var pr = CreatePr();
        var storedResults = new List<ReviewFileResult>();
        var jobRepo = CreateJobRepository(job, storedResults);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, planningJson)),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, synthesisJson)));

        var sut = new AgenticFileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepo,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model" }),
            Substitute.For<ILogger<AgenticFileByFileReviewOrchestrator>>(),
            deterministicReviewFindingGate: new DeterministicReviewFindingGate());

        var result = await sut.ReviewAsync(job, pr, new ReviewSystemContext(null, [], null), CancellationToken.None);

        Assert.Contains("This PR advances the reconsideration and synthesis flow", result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "The most significant backend concern is protocol and token accounting regressions", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Verification retained 1 publishable finding.", result.Summary, StringComparison.Ordinal);
        Assert.Contains("Summary-only findings:", result.Summary, StringComparison.Ordinal);
        Assert.Contains("Synthesis response handling currently reduces observability", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewAsync_GroundingPreservesSafeProgressNarrative()
    {
        const string planningJson =
            """
            {
              "plan_id": "plan-001",
              "anchor_file_path": "src/Foo.cs",
              "concerns": ["Check anchor file."],
              "changed_areas": ["src/Foo.cs"],
              "investigation_tasks": [],
              "no_investigation_reason": "No sibling-file investigation required for this anchor file."
            }
            """;

        const string synthesisJson =
            """
            {
              "summary": "This PR makes meaningful progress on the reconsideration flow, especially around thread-memory fallback retrieval, protocol recording, synthesis parsing, and related test coverage, but the current changes still leave several important correctness and reliability gaps. The most significant issues cluster around protocol/telemetry consistency: token accounting appears vulnerable to double counting when incremental token updates are combined with completion-time totals.",
              "cross_cutting_concerns": [
                {
                  "message": "Potential protocol telemetry verification gap.",
                  "severity": "warning",
                  "category": "cross_cutting",
                  "candidateSummaryText": "Protocol-recorder contract and token-field behavior still have evidence gaps that make end-to-end telemetry correctness harder to verify.",
                  "supportingFindingIds": ["finding-pf-001"],
                  "supportingFiles": ["src/Foo.cs"],
                  "evidenceResolutionState": "resolved",
                  "evidenceSource": "synthesis_payload"
                }
              ]
            }
            """;

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("file summary", []));

        var protocolRecorder = CreateProtocolRecorder();
        var job = CreateJob();
        var pr = CreatePr();
        var storedResults = new List<ReviewFileResult>();
        var jobRepo = CreateJobRepository(job, storedResults);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, planningJson)),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, synthesisJson)));

        var sut = new AgenticFileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepo,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model" }),
            Substitute.For<ILogger<AgenticFileByFileReviewOrchestrator>>(),
            deterministicReviewFindingGate: new DeterministicReviewFindingGate());

        var result = await sut.ReviewAsync(job, pr, new ReviewSystemContext(null, [], null), CancellationToken.None);

        Assert.Contains("This PR makes meaningful progress on the reconsideration flow", result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("The most significant issues cluster around protocol/telemetry consistency", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No publishable findings remained after verification.", result.Summary, StringComparison.Ordinal);
        Assert.Contains("Summary-only findings:", result.Summary, StringComparison.Ordinal);
        Assert.Contains("Protocol-recorder contract and token-field behavior still have evidence gaps", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewAsync_GroundingPreservesNeutralLeadClauseBeforeDetailedRisks()
    {
        const string planningJson =
            """
            {
              "plan_id": "plan-001",
              "anchor_file_path": "src/Foo.cs",
              "concerns": ["Check anchor file."],
              "changed_areas": ["src/Foo.cs"],
              "investigation_tasks": [],
              "no_investigation_reason": "No sibling-file investigation required for this anchor file."
            }
            """;

        const string synthesisJson =
            """
            {
              "summary": "This PR makes broad changes around reconsideration, thread memory lookup, protocol recording, and admin protocol visualization, but the most important risks cluster around protocol/token accounting and end-to-end consistency. The protocol path appears especially fragile and the admin UI still has correctness problems.",
              "cross_cutting_concerns": [
                {
                  "message": "Potential protocol recorder verification gap.",
                  "severity": "warning",
                  "category": "cross_cutting",
                  "candidateSummaryText": "Protocol recorder contract changes need end-to-end verification across implementations and persistence wiring.",
                  "supportingFindingIds": ["finding-pf-001"],
                  "supportingFiles": ["src/Foo.cs"],
                  "evidenceResolutionState": "resolved",
                  "evidenceSource": "synthesis_payload"
                }
              ]
            }
            """;

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("file summary", []));

        var protocolRecorder = CreateProtocolRecorder();
        var job = CreateJob();
        var pr = CreatePr();
        var storedResults = new List<ReviewFileResult>();
        var jobRepo = CreateJobRepository(job, storedResults);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, planningJson)),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, synthesisJson)));

        var sut = new AgenticFileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepo,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model" }),
            Substitute.For<ILogger<AgenticFileByFileReviewOrchestrator>>(),
            deterministicReviewFindingGate: new DeterministicReviewFindingGate());

        var result = await sut.ReviewAsync(job, pr, new ReviewSystemContext(null, [], null), CancellationToken.None);

        Assert.Contains(
            "This PR makes broad changes around reconsideration, thread memory lookup, protocol recording, and admin protocol visualization.", result.Summary,
            StringComparison.Ordinal);
        Assert.DoesNotContain("the most important risks cluster around protocol/token accounting", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("The protocol path appears especially fragile", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No publishable findings remained after verification.", result.Summary, StringComparison.Ordinal);
        Assert.Contains("Summary-only findings:", result.Summary, StringComparison.Ordinal);
        Assert.Contains("Protocol recorder contract changes need end-to-end verification", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewAsync_UnsupportedInvestigationFinding_IsRemovedFromGroundedSummary()
    {
        const string planningJson =
            """
            {
              "plan_id": "plan-001",
              "anchor_file_path": "src/Foo.cs",
              "concerns": ["Check registration wiring."],
              "changed_areas": ["src/Foo.cs", "src/Bar.cs"],
              "investigation_tasks": [
                {
                  "id": "task-001",
                  "task_type": "concern",
                  "concern": "Check registration wiring.",
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
              "summary": "The PR definitely has a missing DI registration across the pipeline.",
              "cross_cutting_concerns": []
            }
            """;

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("file summary", []));

        var protocolRecorder = CreateProtocolRecorder();
        var job = CreateJob();
        var pr = CreatePr();
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
            deterministicReviewFindingGate: new DeterministicReviewFindingGate());

        var result = await sut.ReviewAsync(job, pr, new ReviewSystemContext(null, [], reviewTools), CancellationToken.None);

        Assert.DoesNotContain("missing DI registration across the pipeline", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Potential DI registration gap spans multiple files.", result.Summary, StringComparison.Ordinal);
        Assert.Contains("No publishable or summary-only findings remained after verification.", result.Summary, StringComparison.Ordinal);
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

    private static PullRequest CreatePr()
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
            [new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", "diff")]);
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
}
