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
using MeisterProPR.Infrastructure.AI;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class FileByFileReviewOrchestratorFinalGateTests
{
    [Fact]
    public async Task ReviewAsync_FinalGateMaterializesPublishSummaryOnlyAndDropIntoExpectedOutputs()
    {
        const string synthesisJson =
            """
            {
              "summary": "Base summary.",
              "cross_cutting_concerns": [
                {
                  "message": "Missing DI registration in multiple files.",
                  "severity": "warning",
                  "category": "cross_cutting",
                  "candidateSummaryText": "Potential DI registration gap spans multiple files.",
                  "supportingFindingIds": ["finding-pf-001", "finding-pf-002"],
                  "supportingFiles": ["src/Foo.cs", "src/Bar.cs"],
                  "evidenceResolutionState": "resolved",
                  "evidenceSource": "synthesis_payload"
                },
                {
                  "message": "Architecture concerns span the PR and should be revisited.",
                  "severity": "warning",
                  "category": "architecture",
                  "candidateSummaryText": "Potential architecture concern noted, but it did not meet the publication bar for an actionable review thread.",
                  "supportingFindingIds": [],
                  "supportingFiles": [],
                  "evidenceResolutionState": "missing",
                  "evidenceSource": "synthesis_payload"
                },
                {
                  "message": "Consider refactoring this area for clarity.",
                  "severity": "suggestion",
                  "category": "non_actionable",
                  "supportingFindingIds": [],
                  "supportingFiles": [],
                  "evidenceResolutionState": "missing",
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
                    [
                        new ReviewComment("src/Foo.cs", 12, CommentSeverity.Warning, "Confirmed null dereference in ExecuteAsync."),
                        new ReviewComment("src/Bar.cs", 18, CommentSeverity.Warning, "Missing service registration for handler pipeline."),
                    ]));

        var protocolRecorder = CreateProtocolRecorder();
        var job = CreateJob();
        var pr = CreatePr(CreateFile("src/Foo.cs"), CreateFile("src/Bar.cs"));
        var storedResults = new List<ReviewFileResult>();
        var jobRepo = CreateJobRepository(job, storedResults);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, synthesisJson)));

        var invariantProviders = new IReviewInvariantFactProvider[]
        {
            new StubInvariantFactProvider([]),
        };

        var sut = new FileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepo,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, MaxFileReviewRetries = 3, ModelId = "test-model" }),
            Substitute.For<ILogger<FileByFileReviewOrchestrator>>(),
            deterministicReviewFindingGate: new DeterministicReviewFindingGate(),
            reviewInvariantFactProviders: invariantProviders);

        var result = await sut.ReviewAsync(job, pr, new ReviewSystemContext(null, [], null), CancellationToken.None);

        Assert.DoesNotContain(result.Comments, comment => comment.Message == "Missing DI registration in multiple files.");
        Assert.DoesNotContain(result.Comments, comment => comment.Message == "Architecture concerns span the PR and should be revisited.");
        Assert.DoesNotContain(result.Comments, comment => comment.Message == "Consider refactoring this area for clarity.");
        Assert.Contains("Base summary.", result.Summary);
        Assert.Contains("Potential DI registration gap spans multiple files.", result.Summary);
        Assert.Contains("Potential architecture concern noted", result.Summary);
        Assert.DoesNotContain("Consider refactoring this area for clarity.", result.Summary);

        await protocolRecorder.Received(1)
            .RecordReviewFindingGateEventAsync(
                Arg.Any<Guid>(),
                Arg.Is<string>(name => name == ReviewProtocolEventNames.ReviewFindingGateSummary),
                Arg.Any<string?>(),
                Arg.Is<string?>(output => output != null && output.Contains("\"summaryOnlyCount\":2", StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());

        await protocolRecorder.Received()
            .RecordReviewFindingGateEventAsync(
                Arg.Any<Guid>(),
                Arg.Is<string>(name => name == ReviewProtocolEventNames.ReviewFindingGateDecision),
                Arg.Is<string?>(details => details != null && details.Contains("reasonCodes", StringComparison.Ordinal)),
                Arg.Is<string?>(output => output != null && output.Contains("\"disposition\":\"Publish\"", StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());

        await protocolRecorder.Received()
            .RecordReviewFindingGateEventAsync(
                Arg.Any<Guid>(),
                Arg.Is<string>(name => name == ReviewProtocolEventNames.ReviewFindingGateDecision),
                Arg.Any<string?>(),
                Arg.Is<string?>(output => output != null && output.Contains("\"disposition\":\"SummaryOnly\"", StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());

        await protocolRecorder.Received()
            .RecordReviewFindingGateEventAsync(
                Arg.Any<Guid>(),
                Arg.Is<string>(name => name == ReviewProtocolEventNames.ReviewFindingGateDecision),
                Arg.Any<string?>(),
                Arg.Is<string?>(output => output != null && output.Contains("\"disposition\":\"Drop\"", StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_PersistedLineNumberZero_IsNormalizedBeforeFinalGatePublishing()
    {
        var protocolRecorder = CreateProtocolRecorder();
        var job = CreateJob();
        var pr = CreatePr(CreateFile("src/Foo.cs"));

        var completedResult = new ReviewFileResult(job.Id, "src/Foo.cs");
        completedResult.MarkCompleted(
            "file summary",
            [new ReviewComment("src/Foo.cs", 0, CommentSeverity.Warning, "Confirmed null dereference in ExecuteAsync.")]);

        var storedResults = new List<ReviewFileResult> { completedResult };
        var jobRepo = CreateJobRepository(job, storedResults);

        var aiCore = Substitute.For<IAiReviewCore>();
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis summary")));

        var sut = new FileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepo,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, MaxFileReviewRetries = 3, ModelId = "test-model" }),
            Substitute.For<ILogger<FileByFileReviewOrchestrator>>(),
            deterministicReviewFindingGate: new DeterministicReviewFindingGate(),
            reviewInvariantFactProviders: [new StubInvariantFactProvider([])],
            reviewClaimExtractor: new DeterministicReviewClaimExtractor(),
            reviewFindingVerifier: new DeterministicLocalReviewVerifier());

        var result = await sut.ReviewAsync(job, pr, new ReviewSystemContext(null, [], null), CancellationToken.None);

        var publishedComment = Assert.Single(result.Comments);
        Assert.Equal("Confirmed null dereference in ExecuteAsync.", publishedComment.Message);
        Assert.Null(publishedComment.LineNumber);
    }

    private static ReviewJob CreateJob()
    {
        return new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
    }

    private static ChangedFile CreateFile(string path)
    {
        return new ChangedFile(path, ChangeType.Edit, "content", "diff");
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
