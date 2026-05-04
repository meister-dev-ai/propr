// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
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

public sealed class FileByFileReviewOrchestratorLocalVerificationTests
{
    [Fact]
    public async Task ReviewAsync_LocalContradictedFinding_IsBlockedBeforePersistence()
    {
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewResult(
                    "summary",
                    [
                        new ReviewComment("src/Foo.cs", 10, CommentSeverity.Warning, "ReviewComment.Message may be null when the model omits a message."),
                        new ReviewComment("src/Foo.cs", 18, CommentSeverity.Warning, "Confirmed null dereference in ExecuteAsync."),
                    ]));

        var protocolRecorder = CreateProtocolRecorder();
        var job = CreateJob();
        var file = new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", "diff");
        var pr = CreatePr(file);
        var storedResults = new List<ReviewFileResult>();
        var jobRepo = CreateJobRepository(job, storedResults);
        var chatClient = CreateSynthesisClient();

        var sut = new FileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepo,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model" }),
            Substitute.For<ILogger<FileByFileReviewOrchestrator>>(),
            deterministicReviewFindingGate: new DeterministicReviewFindingGate(),
            reviewInvariantFactProviders:
            [
                new DomainReviewInvariantFactProvider(),
                new PersistenceReviewInvariantFactProvider(),
            ],
            reviewClaimExtractor: new DeterministicReviewClaimExtractor(),
            reviewFindingVerifier: new DeterministicLocalReviewVerifier());

        var result = await sut.ReviewAsync(job, pr, new ReviewSystemContext(null, [], null), CancellationToken.None);

        Assert.Single(storedResults);
        Assert.Single(storedResults[0].Comments!);
        Assert.DoesNotContain(storedResults[0].Comments!, comment => comment.Message.Contains("ReviewComment.Message may be null", StringComparison.Ordinal));
        Assert.Single(result.Comments);
        Assert.Contains(result.Comments, comment => comment.Message == "Confirmed null dereference in ExecuteAsync.");

        await protocolRecorder.Received()
            .RecordVerificationEventAsync(
                Arg.Any<Guid>(),
                Arg.Is<string>(name => name == ReviewProtocolEventNames.VerificationClaimsExtracted),
                Arg.Any<string?>(),
                Arg.Is<string?>(output => output != null && output.Contains(
                    CandidateReviewFinding.ReviewCommentMessageNullableClaimKind,
                    StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());

        await protocolRecorder.Received()
            .RecordVerificationEventAsync(
                Arg.Any<Guid>(),
                Arg.Is<string>(name => name == ReviewProtocolEventNames.VerificationLocalDecision),
                Arg.Any<string?>(),
                Arg.Is<string?>(output => output != null && output.Contains("\"recommendedDisposition\":\"Drop\"", StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_LocalNonVerifiableGenericFinding_IsRemovedFromCommentsAndSummary()
    {
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewResult(
                    "Summary mentions missing helper isCommentRelevanceEvent and a confirmed null dereference.",
                    [
                        new ReviewComment("src/Foo.cs", 10, CommentSeverity.Warning, "The helper method isCommentRelevanceEvent is missing and will fail at runtime."),
                        new ReviewComment("src/Foo.cs", 18, CommentSeverity.Warning, "Confirmed null dereference in ExecuteAsync."),
                    ]));

        var protocolRecorder = CreateProtocolRecorder();
        var job = CreateJob();
        var file = new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", "diff");
        var pr = CreatePr(file);
        var storedResults = new List<ReviewFileResult>();
        var jobRepo = CreateJobRepository(job, storedResults);
        var chatClient = CreateSynthesisClient();

        var sut = new FileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepo,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model" }),
            Substitute.For<ILogger<FileByFileReviewOrchestrator>>(),
            deterministicReviewFindingGate: new DeterministicReviewFindingGate(),
            reviewInvariantFactProviders:
            [
                new DomainReviewInvariantFactProvider(),
                new PersistenceReviewInvariantFactProvider(),
            ],
            reviewClaimExtractor: new DeterministicReviewClaimExtractor(),
            reviewFindingVerifier: new DeterministicLocalReviewVerifier());

        var result = await sut.ReviewAsync(job, pr, new ReviewSystemContext(null, [], null), CancellationToken.None);

        Assert.Single(storedResults);
        Assert.Single(storedResults[0].Comments!);
        Assert.DoesNotContain(storedResults[0].Comments!, comment => comment.Message.Contains("isCommentRelevanceEvent", StringComparison.Ordinal));
        Assert.DoesNotContain("isCommentRelevanceEvent", storedResults[0].PerFileSummary ?? string.Empty, StringComparison.Ordinal);
        Assert.Single(result.Comments);
        Assert.DoesNotContain(result.Comments, comment => comment.Message.Contains("isCommentRelevanceEvent", StringComparison.Ordinal));
        Assert.Contains(result.Comments, comment => comment.Message == "Confirmed null dereference in ExecuteAsync.");

        await protocolRecorder.Received()
            .RecordVerificationEventAsync(
                Arg.Any<Guid>(),
                Arg.Is<string>(name => name == ReviewProtocolEventNames.VerificationLocalDecision),
                Arg.Any<string?>(),
                Arg.Is<string?>(output => output != null &&
                    output.Contains("\"outcomeKind\":\"NonVerifiable\"", StringComparison.Ordinal) &&
                    output.Contains("\"recommendedDisposition\":\"SummaryOnly\"", StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_LineNumberZero_IsNormalizedBeforeLocalVerificationAndPersistence()
    {
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewResult(
                    "summary",
                    [
                        new ReviewComment("src/Foo.cs", 0, CommentSeverity.Warning, "Confirmed null dereference in ExecuteAsync."),
                    ]));

        var protocolRecorder = CreateProtocolRecorder();
        var job = CreateJob();
        var file = new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", "diff");
        var pr = CreatePr(file);
        var storedResults = new List<ReviewFileResult>();
        var jobRepo = CreateJobRepository(job, storedResults);
        var chatClient = CreateSynthesisClient();

        var sut = new FileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepo,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions { MaxFileReviewConcurrency = 1, ModelId = "test-model" }),
            Substitute.For<ILogger<FileByFileReviewOrchestrator>>(),
            deterministicReviewFindingGate: new DeterministicReviewFindingGate(),
            reviewInvariantFactProviders:
            [
                new DomainReviewInvariantFactProvider(),
                new PersistenceReviewInvariantFactProvider(),
            ],
            reviewClaimExtractor: new DeterministicReviewClaimExtractor(),
            reviewFindingVerifier: new DeterministicLocalReviewVerifier());

        var result = await sut.ReviewAsync(job, pr, new ReviewSystemContext(null, [], null), CancellationToken.None);

        var storedResult = Assert.Single(storedResults);
        var storedComment = Assert.Single(storedResult.Comments!);
        Assert.Null(storedComment.LineNumber);

        var finalComment = Assert.Single(result.Comments);
        Assert.Null(finalComment.LineNumber);
        Assert.Equal("Confirmed null dereference in ExecuteAsync.", finalComment.Message);
    }

    private static ReviewJob CreateJob()
    {
        return new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
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

    private static IChatClient CreateSynthesisClient()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis")));
        return chatClient;
    }
}
