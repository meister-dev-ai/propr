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
        var draftResult = new ReviewResult("draft", Array.Empty<ReviewComment>());
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
        var draftResult = new ReviewResult("draft", Array.Empty<ReviewComment>());

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
        protocolRecorder.RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
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

    [Fact]
    public async Task ReviewAsync_WhenMemoryReconsiderationSkipped_DoesNotCallMemoryService()
    {
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
        var draftResult = new ReviewResult("draft", Array.Empty<ReviewComment>());

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

        var orchestrator = new FileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepository,
            chatClient,
            opts,
            logger,
            memoryService: memoryService);

        var context = new ReviewSystemContext(null, [], null)
        {
            SkippedSteps = new ReviewStepSkips([FileByFileReviewStepIds.MemoryReconsideration]),
        };

        await orchestrator.ReviewAsync(job, pr, context, CancellationToken.None);

        await memoryService.DidNotReceive()
            .RetrieveAndReconsiderAsync(
                Arg.Any<Guid>(),
                Arg.Any<ReviewJob>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<ReviewResult>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<float?>());
    }

    [Fact]
    public async Task ReviewAsync_WhenLaterFollowUpSearchReturnsNoMatches_DoesNotReplayEarlierBulkyContextInFull()
    {
        var capturedMessages = new List<List<ChatMessage>>();
        var mockClient = Substitute.For<IChatClient>();
        mockClient
            .GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(messages => capturedMessages.Add(messages.ToList())),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                _ => new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        [
                            new FunctionCallContent(
                                "call-1",
                                "get_file_content",
                                new Dictionary<string, object?>
                                {
                                    ["path"] = "src/Foo.cs",
                                    ["branch"] = "feature/x",
                                    ["startLine"] = 1,
                                    ["endLine"] = 200,
                                }),
                        ])),
                _ => new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        [
                            new FunctionCallContent(
                                "call-2",
                                "search_source_repo",
                                new Dictionary<string, object?>
                                {
                                    ["searchTerm"] = "NoSuchSymbol",
                                    ["fileMask"] = "**/*.cs",
                                }),
                        ])),
                _ => new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {"summary":"done","comments":[],"confidence_evaluations":[{"concern":"correctness","confidence":92}],"investigation_complete":true,"loop_complete":true}
                                            """)),
                _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis summary")));

        var bulkyFileContent = string.Join('\n', Enumerable.Range(1, 160).Select(index => $"line {index}: some bulky repository content"));
        var tools = Substitute.For<IReviewContextTools>();
        tools.GetFileContentAsync("src/Foo.cs", "feature/x", 1, 200, Arg.Any<CancellationToken>())
            .Returns(bulkyFileContent);
        tools.SearchSourceRepoAsync("NoSuchSymbol", "**/*.cs", Arg.Any<CancellationToken>())
            .Returns(
                new RepositorySearchResult(
                    RepositorySearchStatuses.NoMatch,
                    RepositorySearchBranchSides.Source,
                    RepositorySearchPathScopes.Repository,
                    "**/*.cs",
                    [],
                    [],
                    false));

        var aiCore = new ToolAwareAiReviewCore(
            mockClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()),
            Substitute.For<ILogger<ToolAwareAiReviewCore>>());

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

        var jobRepository = Substitute.For<IJobRepository>();
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1)
        {
            Status = JobStatus.Processing,
        };
        var storedResults = new List<ReviewFileResult>();
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

        var orchestrator = new FileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepository,
            mockClient,
            Microsoft.Extensions.Options.Options.Create(new AiReviewOptions()),
            Substitute.For<ILogger<FileByFileReviewOrchestrator>>());

        var pr = CreatePullRequest([new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", "+ new content")]);
        var context = new ReviewSystemContext(null, [], tools);

        var result = await orchestrator.ReviewAsync(job, pr, context, CancellationToken.None);

        Assert.Equal("synthesis summary", result.Summary);
        Assert.True(capturedMessages.Count >= 3);

        var thirdTurnMessages = capturedMessages[2];
        Assert.Contains(
            thirdTurnMessages,
            message => message.Role == ChatRole.System &&
                       (message.Text?.Contains("Working memory summary for prior bulky context:", StringComparison.Ordinal) ?? false));
        Assert.DoesNotContain(
            thirdTurnMessages,
            message => message.Text?.Contains("line 120: some bulky repository content", StringComparison.Ordinal) ?? false);

        await protocolRecorder.Received()
            .RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(),
                ReviewProtocolEventNames.ReviewAgentSessionTurn,
                Arg.Is<string?>(json => json != null && json.Contains("DeltaContext", StringComparison.Ordinal)),
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
