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
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.CommentRelevance;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AI;

public sealed class FileByFileReviewOrchestratorCommentRelevanceFilterTests
{
    [Fact]
    public async Task ReviewAsync_WithSelectedFilter_RunsAfterHardGuardsAndBeforeMemoryReconsideration()
    {
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewResult(
                    "summary",
                    [
                        new ReviewComment("src/Foo.cs", null, CommentSeverity.Warning, "Overall this file has several issues."),
                        new ReviewComment("src/Foo.cs", 8, CommentSeverity.Warning, "Confirmed null dereference at line 8 in `ExecuteAsync`."),
                    ]));

        var protocolRecorder = CreateProtocolRecorder();
        var job = CreateJob();
        var file = new ChangedFile("src/Foo.cs", ChangeType.Edit, "line1\nline2\nline3\nline4\nline5\nline6\nline7\nline8\nline9\nline10", "@@ -1,10 +1,10 @@");
        var pr = CreatePr(file);
        var storedResults = new List<ReviewFileResult>();
        var jobRepo = CreateJobRepository(job, storedResults);
        var chatClient = CreateSynthesisClient();
        var memoryService = Substitute.For<IThreadMemoryService>();

        IReadOnlyList<ReviewComment>? memoryInput = null;
        memoryService.RetrieveAndReconsiderAsync(
                Arg.Any<Guid>(),
                Arg.Any<ReviewJob>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<ReviewResult>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<float?>())
            .Returns(callInfo =>
            {
                memoryInput = callInfo.Arg<ReviewResult>().Comments;
                return callInfo.Arg<ReviewResult>();
            });

        var filterRegistry = new CommentRelevanceFilterRegistry(
            [
                new HeuristicCommentRelevanceFilter(), new PassThroughCommentRelevanceFilter(),
                new HybridCommentRelevanceFilter(Substitute.For<ICommentRelevanceAmbiguityEvaluator>()),
            ],
            new CommentRelevanceFilterSelection("heuristic-v1"));

        var sut = new FileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepo,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()),
            Substitute.For<ILogger<FileByFileReviewOrchestrator>>(),
            memoryService: memoryService,
            commentRelevanceFilterRegistry: filterRegistry);

        await sut.ReviewAsync(job, pr, new ReviewSystemContext(null, [], null), CancellationToken.None);

        Assert.NotNull(memoryInput);
        Assert.Single(memoryInput!);
        Assert.Equal("Confirmed null dereference at line 8 in `ExecuteAsync`.", memoryInput[0].Message);
        await protocolRecorder.Received(1)
            .RecordCommentRelevanceEventAsync(
                Arg.Any<Guid>(),
                Arg.Is<string>(name => name == ReviewProtocolEventNames.CommentRelevanceFilterOutput),
                Arg.Any<string?>(),
                Arg.Is<string?>(output => output != null && output.Contains("implementationId", StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WhenSelectedFilterMissing_FailsOpenAndRecordsSelectionFallback()
    {
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewResult(
                    "summary",
                    [new ReviewComment("src/Foo.cs", 8, CommentSeverity.Warning, "Confirmed null dereference at line 8 in `ExecuteAsync`.")]));

        var protocolRecorder = CreateProtocolRecorder();
        var job = CreateJob();
        var file = new ChangedFile("src/Foo.cs", ChangeType.Edit, "line1\nline2\nline3\nline4\nline5\nline6\nline7\nline8\nline9\nline10", "@@ -1,10 +1,10 @@");
        var pr = CreatePr(file);
        var storedResults = new List<ReviewFileResult>();
        var jobRepo = CreateJobRepository(job, storedResults);
        var chatClient = CreateSynthesisClient();
        var filterRegistry = new CommentRelevanceFilterRegistry([], new CommentRelevanceFilterSelection("missing-filter"));

        var sut = new FileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepo,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()),
            Substitute.For<ILogger<FileByFileReviewOrchestrator>>(),
            commentRelevanceFilterRegistry: filterRegistry);

        var result = await sut.ReviewAsync(job, pr, new ReviewSystemContext(null, [], null), CancellationToken.None);

        Assert.Single(result.Comments);
        await protocolRecorder.Received(1)
            .RecordCommentRelevanceEventAsync(
                Arg.Any<Guid>(),
                Arg.Is<string>(name => name == ReviewProtocolEventNames.CommentRelevanceFilterSelectionFallback),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_HybridEvaluatorAiWork_AddsProtocolTokensAndDedicatedEvent()
    {
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewResult("summary", [new ReviewComment("src/Foo.cs", null, CommentSeverity.Warning, "Critical issue may exist in another file.")]));

        var protocolRecorder = CreateProtocolRecorder();
        var job = CreateJob();
        var file = new ChangedFile("src/Foo.cs", ChangeType.Edit, "line1\nline2\nline3\nline4\nline5\nline6\nline7\nline8\nline9\nline10", "@@ -1,10 +1,10 @@");
        var pr = CreatePr(file);
        var storedResults = new List<ReviewFileResult>();
        var jobRepo = CreateJobRepository(job, storedResults);
        var chatClient = CreateSynthesisClient();
        var evaluator = Substitute.For<ICommentRelevanceAmbiguityEvaluator>();
        evaluator.EvaluateAsync(Arg.Any<CommentRelevanceFilterRequest>(), Arg.Any<IReadOnlyList<ReviewComment>>(), Arg.Any<CancellationToken>())
            .Returns(
                new CommentRelevanceAmbiguityEvaluationResult(
                    [
                        new CommentRelevanceFilterDecision(
                            CommentRelevanceFilterDecision.DiscardDecision,
                            new ReviewComment("src/Foo.cs", null, CommentSeverity.Warning, "Critical issue may exist in another file."),
                            [CommentRelevanceReasonCodes.UnverifiableCrossFileClaim],
                            CommentRelevanceFilterDecision.AiAdjudicationSource),
                    ],
                    true,
                    new FilterAiTokenUsage("hybrid-v1", "src/Foo.cs", 320, 71, AiConnectionModelCategory.Default, "test-evaluator"),
                    [],
                    [],
                    null));

        var filterRegistry = new CommentRelevanceFilterRegistry(
            [new HybridCommentRelevanceFilter(evaluator), new HeuristicCommentRelevanceFilter(), new PassThroughCommentRelevanceFilter()],
            new CommentRelevanceFilterSelection("hybrid-v1"));

        var sut = new FileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepo,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()),
            Substitute.For<ILogger<FileByFileReviewOrchestrator>>(),
            commentRelevanceFilterRegistry: filterRegistry);

        await sut.ReviewAsync(job, pr, new ReviewSystemContext(null, [], null), CancellationToken.None);

        await protocolRecorder.Received(1)
            .RecordAiCallAsync(
                Arg.Any<Guid>(),
                0,
                320,
                71,
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(),
                ReviewProtocolEventNames.CommentRelevanceEvaluatorAiCall);
        await protocolRecorder.Received(1)
            .AddTokensAsync(
                Arg.Any<Guid>(),
                320,
                71,
                AiConnectionModelCategory.Default,
                "test-evaluator",
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WhenCommentRelevanceFilterIsCanceled_PropagatesCancellationWithoutFallback()
    {
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewResult(
                    "summary",
                    [new ReviewComment("src/Foo.cs", 8, CommentSeverity.Warning, "Confirmed null dereference at line 8 in `ExecuteAsync`.")]));

        var protocolRecorder = CreateProtocolRecorder();
        var job = CreateJob();
        var file = new ChangedFile("src/Foo.cs", ChangeType.Edit, "line1\nline2\nline3\nline4\nline5\nline6\nline7\nline8\nline9\nline10", "@@ -1,10 +1,10 @@");
        var pr = CreatePr(file);
        var storedResults = new List<ReviewFileResult>();
        var jobRepo = CreateJobRepository(job, storedResults);
        var chatClient = CreateSynthesisClient();
        var canceledFilter = Substitute.For<ICommentRelevanceFilter>();
        canceledFilter.ImplementationId.Returns("canceling-filter");
        canceledFilter.ImplementationVersion.Returns("1.0.0");
        canceledFilter.FilterAsync(Arg.Any<CommentRelevanceFilterRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<CommentRelevanceFilterResult>>(_ => throw new OperationCanceledException());

        var filterRegistry = new CommentRelevanceFilterRegistry(
            [canceledFilter],
            new CommentRelevanceFilterSelection("canceling-filter"));

        var sut = new FileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepo,
            chatClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()),
            Substitute.For<ILogger<FileByFileReviewOrchestrator>>(),
            commentRelevanceFilterRegistry: filterRegistry);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            sut.ReviewAsync(job, pr, new ReviewSystemContext(null, [], null), CancellationToken.None));

        await protocolRecorder.DidNotReceive()
            .RecordCommentRelevanceEventAsync(
                Arg.Any<Guid>(),
                Arg.Is<string>(name => name == ReviewProtocolEventNames.CommentRelevanceFilterDegraded),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>());
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

    private static IChatClient CreateSynthesisClient()
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis summary")));
        return chatClient;
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
            .Returns(Guid.NewGuid());
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
        recorder.RecordCommentRelevanceEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        recorder.RecordReviewFindingGateEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        recorder.AddTokensAsync(
                Arg.Any<Guid>(),
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<AiConnectionModelCategory?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return recorder;
    }
}
