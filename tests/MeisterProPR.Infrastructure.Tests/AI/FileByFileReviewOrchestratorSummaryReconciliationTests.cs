// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.ReviewFindingGate;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class FileByFileReviewOrchestratorSummaryReconciliationTests
{
    [Fact]
    public async Task ReviewAsync_FinalSummaryIsReconciledBeforePublication()
    {
        const string synthesisJson =
            """
            {
              "summary": "The PR definitely has a missing DI registration across the pipeline.",
              "cross_cutting_concerns": [
                {
                  "message": "Missing DI registration across the pipeline.",
                  "severity": "suggestion",
                  "category": "non_actionable",
                  "supportingFindingIds": [],
                  "supportingFiles": [],
                  "evidenceResolutionState": "missing",
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

        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        protocolRecorder.BeginAsync(
                Arg.Any<Guid>(),
                Arg.Any<int>(),
                Arg.Any<string?>(),
                Arg.Any<Guid?>(),
                Arg.Any<AiConnectionModelCategory?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());
        protocolRecorder.SetCompletedAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordAiCallAsync(
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
        protocolRecorder.RecordReviewFindingGateEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.RecordVerificationEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
        var pr = new PullRequest(
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

        var storedResults = new List<ReviewFileResult>();
        var jobRepo = Substitute.For<IJobRepository>();
        jobRepo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        jobRepo.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                storedResults.Add(call.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        jobRepo.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, synthesisJson)));

        var sut = new FileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepo,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model" }),
            Substitute.For<ILogger<FileByFileReviewOrchestrator>>(),
            deterministicReviewFindingGate: new DeterministicReviewFindingGate());

        var result = await sut.ReviewAsync(job, pr, new ReviewSystemContext(null, [], null), CancellationToken.None);

        Assert.DoesNotContain("missing DI registration", result.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 publishable finding", result.Summary);
        Assert.Contains("Potential architecture concern noted", result.Summary);
        await protocolRecorder.Received()
            .RecordVerificationEventAsync(
                Arg.Any<Guid>(),
                ReviewProtocolEventNames.SummaryReconciliation,
                Arg.Any<string?>(),
                Arg.Is<string?>(output => output != null && output.Contains("deterministic_summary_rewrite", StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
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

        foreach (var result in results)
        {
            job.FileReviewResults.Add(result);
        }

        return job;
    }
}
