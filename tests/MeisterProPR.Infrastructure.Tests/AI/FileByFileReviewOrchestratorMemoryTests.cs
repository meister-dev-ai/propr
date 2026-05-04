// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AI;

/// <summary>
///     Unit tests for the memory reconsideration step in <see cref="FileByFileReviewOrchestrator" /> (T031).
/// </summary>
public sealed class FileByFileReviewOrchestratorMemoryTests
{
    [Fact]
    public async Task ReviewAsync_WhenMemoryServiceInjected_CallsRetrieveAndReconsiderAfterAiReview()
    {
        // Arrange
        var aiCore = Substitute.For<IAiReviewCore>();
        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        var jobRepository = Substitute.For<IJobRepository>();
        var chatClient = Substitute.For<IChatClient>();
        var opts = Microsoft.Extensions.Options.Options.Create(new AiReviewOptions());
        var logger = Substitute.For<ILogger<FileByFileReviewOrchestrator>>();
        var memoryService = Substitute.For<IThreadMemoryService>();

        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
        job.Status = JobStatus.Processing;

        var file = new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", "- old\n+ new");
        var pr = CreatePullRequest([file]);
        var draftResult = new ReviewResult("draft", []);
        var reconsidered = new ReviewResult("reconsidered", []);

        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(draftResult);

        jobRepository.GetByIdWithFileResultsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(job));

        protocolRecorder.BeginAsync(
                Arg.Any<Guid>(),
                Arg.Any<int>(),
                Arg.Any<string?>(),
                Arg.Any<Guid?>(),
                Arg.Any<AiConnectionModelCategory?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());

        memoryService.RetrieveAndReconsiderAsync(
                Arg.Any<Guid>(),
                Arg.Any<ReviewJob>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<ReviewResult>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<float?>())
            .Returns(reconsidered);

        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")));

        // Stub synthesis
        aiCore.ReviewAsync(
                Arg.Is<PullRequest>(p => p.ChangedFiles.Count == 0 || p.ChangedFiles.Any(f => f.Path == "src/Foo.cs")),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>())
            .Returns(draftResult);

        var orchestrator = new FileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepository,
            chatClient,
            opts,
            logger,
            memoryService: memoryService);

        var context = new ReviewSystemContext(null, [], null);

        // Act
        await orchestrator.ReviewAsync(job, pr, context, CancellationToken.None);

        // Assert: memory service was called with at least one file
        await memoryService.Received()
            .RetrieveAndReconsiderAsync(
                Arg.Any<Guid>(),
                Arg.Any<ReviewJob>(),
                "src/Foo.cs",
                Arg.Any<string?>(),
                Arg.Any<ReviewResult>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<float?>());
    }

    [Fact]
    public async Task ReviewAsync_WhenMemoryServiceIsNull_PassesThroughOriginalResult()
    {
        // Arrange
        var aiCore = Substitute.For<IAiReviewCore>();
        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        var jobRepository = Substitute.For<IJobRepository>();
        var chatClient = Substitute.For<IChatClient>();
        var opts = Microsoft.Extensions.Options.Options.Create(new AiReviewOptions());
        var logger = Substitute.For<ILogger<FileByFileReviewOrchestrator>>();

        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
        job.Status = JobStatus.Processing;

        var file = new ChangedFile("src/Bar.cs", ChangeType.Edit, "content", "- old\n+ new");
        var pr = CreatePullRequest([file]);
        var draftResult = new ReviewResult("draft", []);

        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(draftResult);
        jobRepository.GetByIdWithFileResultsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(job));
        protocolRecorder.BeginAsync(
                Arg.Any<Guid>(),
                Arg.Any<int>(),
                Arg.Any<string?>(),
                Arg.Any<Guid?>(),
                Arg.Any<AiConnectionModelCategory?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")));

        // No memory service passed (null)
        var orchestrator = new FileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepository,
            chatClient,
            opts,
            logger);

        var context = new ReviewSystemContext(null, [], null);

        // Act + Assert — should not throw
        var ex = await Record.ExceptionAsync(() =>
            orchestrator.ReviewAsync(job, pr, context, CancellationToken.None));

        Assert.Null(ex);
    }

    [Fact]
    public async Task ReviewAsync_WhenMemoryServiceIsNull_StillReturnsFreshCommentCandidates()
    {
        var aiCore = Substitute.For<IAiReviewCore>();
        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        var chatClient = Substitute.For<IChatClient>();
        var opts = Microsoft.Extensions.Options.Options.Create(new AiReviewOptions());
        var logger = Substitute.For<ILogger<FileByFileReviewOrchestrator>>();

        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
        job.Status = JobStatus.Processing;

        var comment = new ReviewComment("src/Baz.cs", 8, CommentSeverity.Warning, "fresh issue");
        var file = new ChangedFile("src/Baz.cs", ChangeType.Edit, "content", "- old\n+ new");
        var pr = CreatePullRequest([file]);
        var draftResult = new ReviewResult("draft", [comment]);
        var storedResults = new List<ReviewFileResult>();

        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(draftResult);
        var jobRepository = Substitute.For<IJobRepository>();
        jobRepository.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        jobRepository.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                storedResults.Add(callInfo.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        jobRepository.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        protocolRecorder.BeginAsync(
                Arg.Any<Guid>(),
                Arg.Any<int>(),
                Arg.Any<string?>(),
                Arg.Any<Guid?>(),
                Arg.Any<AiConnectionModelCategory?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis summary")));

        var orchestrator = new FileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepository,
            chatClient,
            opts,
            logger);

        var context = new ReviewSystemContext(null, [], null);

        var result = await orchestrator.ReviewAsync(job, pr, context, CancellationToken.None);

        Assert.Contains(result.Comments, reviewComment => reviewComment.Message == "fresh issue");
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

    private static PullRequest CreatePullRequest(IReadOnlyList<ChangedFile>? files = null)
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
            files ?? new List<ChangedFile>().AsReadOnly());
    }
}
