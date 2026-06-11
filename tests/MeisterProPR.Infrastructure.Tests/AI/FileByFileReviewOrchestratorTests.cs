// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;
using MeisterProPR.Infrastructure.Features.Reviewing.Diagnostics.Persistence;
using MeisterProPR.Infrastructure.Features.Reviewing.Execution.Strategies;
using MeisterProPR.ProRV.Abstractions;
using MeisterProPR.ProRV.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterProPR.Infrastructure.Tests.AI;

public class FileByFileReviewOrchestratorTests
{
    private static IOptions<AiReviewOptions> DefaultOptions()
    {
        return Microsoft.Extensions.Options.Options.Create(
            new AiReviewOptions { MaxFileReviewConcurrency = 1, MaxFileReviewRetries = 3, ModelId = "test-model" });
    }

    private static IOptions<AiReviewOptions> CreateOptions(Action<AiReviewOptions> configure)
    {
        var options = new AiReviewOptions { MaxFileReviewConcurrency = 1, MaxFileReviewRetries = 3, ModelId = "test-model" };
        configure(options);
        return Microsoft.Extensions.Options.Options.Create(options);
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

    private static ReviewSystemContext CreateContext()
    {
        return new ReviewSystemContext(null, [], null);
    }

    private static ReviewResult CreateResult(string summary = "ok")
    {
        return new ReviewResult(summary, new List<ReviewComment>().AsReadOnly());
    }

    private static FileByFileReviewOrchestrator CreateOrchestrator(
        IAiReviewCore aiCore,
        IProtocolRecorder protocolRecorder,
        IJobRepository jobRepository,
        IChatClient chatClient,
        IOptions<AiReviewOptions>? options = null,
        IAiConnectionRepository? aiConnectionRepository = null,
        IAiChatClientFactory? aiClientFactory = null,
        IAiRuntimeResolver? aiRuntimeResolver = null,
        IReviewPipelineProfileProvider? pipelineProfileProvider = null,
        IProRVPrefilter? proRvPrefilter = null)
    {
        return new FileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepository,
            chatClient,
            options ?? DefaultOptions(),
            Substitute.For<ILogger<FileByFileReviewOrchestrator>>(),
            aiConnectionRepository,
            aiClientFactory,
            null,
            aiRuntimeResolver,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            pipelineProfileProvider,
            proRvPrefilter);
    }

    private static IJobRepository CreateJobRepo(ReviewJob? jobWithResults = null)
    {
        var repo = Substitute.For<IJobRepository>();
        repo.GetByIdWithFileResultsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(jobWithResults);
        repo.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        repo.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return repo;
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
        recorder.RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return recorder;
    }

    // T033 — 3-file PR results in 3 IAiReviewCore.ReviewAsync calls
    [Fact]
    public async Task ReviewAsync_ThreeFilePr_CallsAiCoreThreeTimes()
    {
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(CreateResult());

        var job = CreateJob();
        var files = new[] { CreateFile("a.cs"), CreateFile("b.cs"), CreateFile("c.cs") };
        var pr = CreatePr(files);

        var repo = CreateJobRepo();
        repo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(job);

        // Setup synthesis chatclient to return something
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis")));
        var sut2 = CreateOrchestrator(aiCore, CreateProtocolRecorder(), repo, chatClient);

        await sut2.ReviewAsync(job, pr, CreateContext(), CancellationToken.None);

        await aiCore.Received(3)
            .ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_FileByFileStrategy_DoesNotTraverseAgenticPlanningPath()
    {
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewResult("file summary", [new ReviewComment("src/Foo.cs", 12, CommentSeverity.Warning, "Direct file finding remains publishable.")]));

        var job = CreateJob();
        job.SelectReviewStrategy(
            ReviewStrategy.FileByFile,
            ReviewStrategySelectionSource.ClientDefault,
            ReviewComparisonMode.Single,
            ReviewPublicationMode.Publish,
            null);

        var pr = CreatePr(CreateFile("src/Foo.cs"));
        var storedResults = new List<ReviewFileResult>();
        var repo = Substitute.For<IJobRepository>();
        repo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repo.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                storedResults.Add(callInfo.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repo.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis summary")));

        var sut = CreateOrchestrator(aiCore, CreateProtocolRecorder(), repo, chatClient);

        var result = await sut.ReviewAsync(job, pr, CreateContext(), CancellationToken.None);

        await chatClient.Received(1)
            .GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());

        var publishedComment = Assert.Single(result.Comments);
        Assert.Equal("Direct file finding remains publishable.", publishedComment.Message);
    }

    // T033 — A failed file does not stop remaining files
    [Fact]
    public async Task ReviewAsync_OneFileFails_OtherFilesStillReviewed()
    {
        var aiCore = Substitute.For<IAiReviewCore>();
        var callCount = 0;

        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var count = Interlocked.Increment(ref callCount);
                // Second call (index 1) fails
                if (count == 2)
                {
                    throw new InvalidOperationException("AI failed for file 2");
                }

                return Task.FromResult(CreateResult($"ok-{count}"));
            });

        var job = CreateJob();
        var files = new[] { CreateFile("a.cs"), CreateFile("b.cs"), CreateFile("c.cs") };
        var pr = CreatePr(files);

        var repo = CreateJobRepo();
        repo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(job);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis")));

        var sut = CreateOrchestrator(aiCore, CreateProtocolRecorder(), repo, chatClient);

        await Assert.ThrowsAsync<PartialReviewFailureException>(() =>
            sut.ReviewAsync(job, pr, CreateContext(), CancellationToken.None));

        // All 3 files were attempted despite 1 failure (concurrency = 1 processes sequentially but all run)
        await aiCore.Received(3)
            .ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
    }

    // T033 — Retry-resume skips completed files
    [Fact]
    public async Task ReviewAsync_RetryResume_SkipsAlreadyCompletedFiles()
    {
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(CreateResult());

        var job = CreateJob();
        var aFile = CreateFile("a.cs");
        var bFile = CreateFile("b.cs");
        var cFile = CreateFile("c.cs");
        var pr = CreatePr(aFile, bFile, cFile);

        // Simulate a.cs already completed from previous attempt
        var completedResult = new ReviewFileResult(job.Id, "a.cs");
        completedResult.MarkCompleted("a done", new List<ReviewComment>().AsReadOnly());

        // Must return the same job id
        var repo = Substitute.For<IJobRepository>();
        // Return job with one completed file result
        repo.GetByIdWithFileResultsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(null));
        // We need a ReviewJob whose FileReviewResults contains completedResult
        // Use a sub that supports FileReviewResults collection inspection
        repo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                var mockJob = new ReviewJob(
                    job.Id,
                    job.ClientId,
                    job.OrganizationUrl,
                    job.ProjectId,
                    job.RepositoryId,
                    job.PullRequestId,
                    job.IterationId);
                mockJob.FileReviewResults.Add(completedResult);
                return Task.FromResult<ReviewJob?>(mockJob);
            });
        repo.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        repo.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis")));

        var sut = CreateOrchestrator(aiCore, CreateProtocolRecorder(), repo, chatClient);

        await sut.ReviewAsync(job, pr, CreateContext(), CancellationToken.None);

        // Only b.cs and c.cs should be reviewed (a.cs is already complete)
        await aiCore.Received(2)
            .ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
    }

    // T034 — All file comments appear in aggregated result
    [Fact]
    public async Task ReviewAsync_AllFileComments_AppearInSynthesisResult()
    {
        var commentA = new ReviewComment("a.cs", 1, CommentSeverity.Warning, "issue in A");
        var commentB = new ReviewComment("b.cs", 2, CommentSeverity.Error, "issue in B");

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(
                Arg.Is<PullRequest>(p => p.ChangedFiles.Any(f => f.Path == "a.cs")),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("summary A", new List<ReviewComment> { commentA }.AsReadOnly()));
        aiCore.ReviewAsync(
                Arg.Is<PullRequest>(p => p.ChangedFiles.Any(f => f.Path == "b.cs")),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("summary B", new List<ReviewComment> { commentB }.AsReadOnly()));

        var job = CreateJob();
        var pr = CreatePr(CreateFile("a.cs"), CreateFile("b.cs"));

        // Mock repo: initially no file results, then returns them after updates
        var storedResults = new List<ReviewFileResult>();
        var repo = Substitute.For<IJobRepository>();
        repo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repo.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                storedResults.Add(callInfo.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repo.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis summary")));

        var sut = CreateOrchestrator(aiCore, CreateProtocolRecorder(), repo, chatClient);

        var result = await sut.ReviewAsync(job, pr, CreateContext(), CancellationToken.None);

        Assert.Equal("synthesis summary", result.Summary);
        Assert.Contains(result.Comments, c => c.Message == "issue in A");
        Assert.Contains(result.Comments, c => c.Message == "issue in B");
    }

    [Fact]
    public async Task ReviewAsync_CarriedForwardResults_DoNotContributePostingCandidates()
    {
        var carriedForwardComment = new ReviewComment("legacy.cs", 10, CommentSeverity.Warning, "legacy issue");
        var freshComment = new ReviewComment("fresh.cs", 20, CommentSeverity.Warning, "fresh issue");

        var priorResult = new ReviewFileResult(Guid.NewGuid(), "legacy.cs");
        priorResult.MarkCompleted("legacy summary", [carriedForwardComment]);

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("fresh summary", [freshComment]));

        var job = CreateJob();
        var pr = CreatePr(CreateFile("fresh.cs"));

        var storedResults = new List<ReviewFileResult>
        {
            ReviewFileResult.CreateCarriedForward(job.Id, priorResult),
        };

        var repo = Substitute.For<IJobRepository>();
        repo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repo.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                storedResults.Add(callInfo.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repo.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis summary")));

        var sut = CreateOrchestrator(aiCore, CreateProtocolRecorder(), repo, chatClient);

        var result = await sut.ReviewAsync(job, pr, CreateContext(), CancellationToken.None);

        Assert.Single(result.Comments);
        Assert.Equal("fresh issue", result.Comments[0].Message);
        Assert.DoesNotContain(result.Comments, comment => comment.Message == "legacy issue");
        Assert.Equal(1, result.CarriedForwardCandidatesSkipped);
    }

    // T034 — Synthesis fallback triggers when IChatClient throws
    [Fact]
    public async Task ReviewAsync_SynthesisFails_FallsBackToConcatenatedSummaries()
    {
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var pr = ci.Arg<PullRequest>();
                var file = pr.ChangedFiles[0];
                return Task.FromResult(new ReviewResult($"Summary for {file.Path}", new List<ReviewComment>().AsReadOnly()));
            });

        var job = CreateJob();
        var pr = CreatePr(CreateFile("a.cs"), CreateFile("b.cs"));

        var storedResults = new List<ReviewFileResult>();
        var repo = Substitute.For<IJobRepository>();
        repo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repo.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                storedResults.Add(ci.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repo.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("synthesis service unavailable"));

        var sut = CreateOrchestrator(aiCore, CreateProtocolRecorder(), repo, chatClient);

        var result = await sut.ReviewAsync(job, pr, CreateContext(), CancellationToken.None);

        // Fallback: summary is a concatenation of per-file summaries
        Assert.Contains("a.cs", result.Summary);
        Assert.Contains("b.cs", result.Summary);
    }

    [Fact]
    public async Task ReviewAsync_SynthesisFails_RecordsProtocolFailureEvent()
    {
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var pr = ci.Arg<PullRequest>();
                var file = pr.ChangedFiles[0];
                return Task.FromResult(new ReviewResult($"Summary for {file.Path}", new List<ReviewComment>().AsReadOnly()));
            });

        var protocolRecorder = CreateProtocolRecorder();
        var job = CreateJob();
        var pr = CreatePr(CreateFile("a.cs"));

        var storedResults = new List<ReviewFileResult>();
        var repo = Substitute.For<IJobRepository>();
        repo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repo.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                storedResults.Add(ci.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repo.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("synthesis service unavailable"));

        var sut = CreateOrchestrator(aiCore, protocolRecorder, repo, chatClient);

        await sut.ReviewAsync(job, pr, CreateContext(), CancellationToken.None);

        await protocolRecorder.Received()
            .RecordAiCallAsync(
                Arg.Any<Guid>(),
                1,
                0,
                0,
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Is<string?>(output => output == null),
                Arg.Any<CancellationToken>(),
                Arg.Any<string?>(),
                Arg.Is<string?>(error => error != null && error.Contains("synthesis service unavailable")));
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
        foreach (var r in results)
        {
            job.FileReviewResults.Add(r);
        }

        return job;
    }

    // T013 — Files matching ExclusionRules are not dispatched to IAiReviewCore
    [Fact]
    public async Task ReviewAsync_ExcludedFile_NotPassedToAiCore()
    {
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(CreateResult()));

        var protocolRecorder = CreateProtocolRecorder();
        var job = CreateJob();

        var storedResults = new List<ReviewFileResult>();
        var jobRepo = CreateJobRepo();
        jobRepo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        jobRepo.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                storedResults.Add(ci.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        jobRepo.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis")));

        var pr = CreatePr(
            CreateFile("src/Migrations/20260101_Init.Designer.cs"), // will be excluded
            CreateFile("src/Application/Service.cs")); // will be reviewed

        var context = new ReviewSystemContext(null, [], null)
        {
            ExclusionRules = ReviewExclusionRules.Default,
        };

        var sut = CreateOrchestrator(aiCore, protocolRecorder, jobRepo, chatClient);
        await sut.ReviewAsync(job, pr, context, CancellationToken.None);

        // Only Service.cs should be sent to the AI; Designer.cs is excluded
        await aiCore.Received(1)
            .ReviewAsync(
                Arg.Is<PullRequest>(p => p.ChangedFiles.Any(f => f.Path == "src/Application/Service.cs")),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>());
        await aiCore.DidNotReceive()
            .ReviewAsync(
                Arg.Is<PullRequest>(p => p.ChangedFiles.Any(f => f.Path.EndsWith(".Designer.cs"))),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>());
    }

    // T013 — Excluded files result in a protocol entry with zero tokens
    [Fact]
    public async Task ReviewAsync_ExcludedFile_ProtocolRecordedWithZeroTokens()
    {
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateResult()));

        var protocolRecorder = CreateProtocolRecorder();
        var job = CreateJob();

        var storedResults = new List<ReviewFileResult>();
        var jobRepo = CreateJobRepo();
        jobRepo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        jobRepo.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                storedResults.Add(ci.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        jobRepo.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis")));

        var pr = CreatePr(CreateFile("src/Migrations/20260101_Init.Designer.cs"));

        var context = new ReviewSystemContext(null, [], null)
        {
            ExclusionRules = ReviewExclusionRules.Default,
        };

        var sut = CreateOrchestrator(aiCore, protocolRecorder, jobRepo, chatClient);
        await sut.ReviewAsync(job, pr, context, CancellationToken.None);

        await protocolRecorder.Received(1)
            .SetCompletedAsync(
                Arg.Any<Guid>(),
                "Excluded",
                0,
                0,
                0,
                0,
                null,
                Arg.Any<CancellationToken>());
    }

    // ─── T043: tier client resolution ────────────────────────────────────────────

    [Fact]
    public async Task ReviewAsync_TierConnectionExists_UsesTierClientViaContext()
    {
        // Arrange
        var job = CreateJob();
        var pr = CreatePr(
            new ChangedFile(
                "src/BigService.cs",
                ChangeType.Edit,
                "content",
                string.Concat(Enumerable.Repeat("+line\n", 200)))); // >150 lines → High tier
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(CreateResult());

        var defaultChatClient = Substitute.For<IChatClient>();
        defaultChatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis")));

        var tierClient = Substitute.For<IChatClient>();
        var aiClientFactory = Substitute.For<IAiChatClientFactory>();
        aiClientFactory.CreateClient(Arg.Any<string>(), Arg.Any<string?>()).Returns(tierClient);

        var tierDto = AiConnectionTestFactory.CreateChatConnection(
            job.ClientId,
            "gpt-4o-high",
            AiPurpose.ReviewHighEffort,
            baseUrl: "https://high.openai.azure.com/");
        var aiConnectionRepo = Substitute.For<IAiConnectionRepository>();
        aiConnectionRepo.GetForTierAsync(
                job.ClientId,
                AiConnectionModelCategory.HighEffort,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AiConnectionDto?>(tierDto));

        var jobRepo = CreateJobRepo();
        jobRepo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        var sut = CreateOrchestrator(
            aiCore,
            CreateProtocolRecorder(),
            jobRepo,
            defaultChatClient,
            aiConnectionRepository: aiConnectionRepo,
            aiClientFactory: aiClientFactory);

        // Act
        await sut.ReviewAsync(job, pr, CreateContext(), CancellationToken.None);

        // Assert: GetForTierAsync was called for the High tier (once per-file and once for synthesis)
        await aiConnectionRepo.Received(2)
            .GetForTierAsync(job.ClientId, AiConnectionModelCategory.HighEffort, Arg.Any<CancellationToken>());

        // Assert: aiCore.ReviewAsync received a context with TierChatClient set to the tier client
        await aiCore.Received(1)
            .ReviewAsync(
                Arg.Any<PullRequest>(),
                Arg.Is<ReviewSystemContext>(ctx => ctx.TierChatClient == tierClient && ctx.ModelId == "gpt-4o-high"),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_NoTierConnection_FallsBackToEffectiveClient()
    {
        // Arrange — repo returns null for tier lookup
        var job = CreateJob();
        var pr = CreatePr(
            new ChangedFile(
                "src/Simple.cs",
                ChangeType.Edit,
                "content",
                string.Concat(Enumerable.Repeat("+line\n", 5)))); // Low tier
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(CreateResult());

        var defaultChatClient = Substitute.For<IChatClient>();
        defaultChatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis")));

        var aiConnectionRepo = Substitute.For<IAiConnectionRepository>();
        aiConnectionRepo.GetForTierAsync(
                Arg.Any<Guid>(),
                Arg.Any<AiConnectionModelCategory>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AiConnectionDto?>(null));

        var jobRepo = CreateJobRepo();
        jobRepo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        var sut = CreateOrchestrator(
            aiCore,
            CreateProtocolRecorder(),
            jobRepo,
            defaultChatClient,
            aiConnectionRepository: aiConnectionRepo,
            aiClientFactory: Substitute.For<IAiChatClientFactory>());

        // Act
        await sut.ReviewAsync(job, pr, CreateContext(), CancellationToken.None);

        // Assert: context TierChatClient is set to the injected default (no tier → falls back to effectiveClient)
        await aiCore.Received(1)
            .ReviewAsync(
                Arg.Any<PullRequest>(),
                Arg.Is<ReviewSystemContext>(ctx => ctx.TierChatClient == defaultChatClient),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_ProRvPrefilterRuntimeExists_InjectsFocusedReviewGuidanceIntoContext()
    {
        var job = CreateJob();
        job.SelectReviewStrategy(
            ReviewStrategy.FileByFile,
            ReviewStrategySelectionSource.ClientDefault,
            ReviewComparisonMode.Single,
            ReviewPublicationMode.Publish,
            null,
            ReviewPipelineProfileProvider.FileByFileBaselineProfileId);
        var pr = CreatePr(
            new ChangedFile(
                "src/Web/ErrorPage.cs",
                ChangeType.Edit,
                "content",
                "+ var page = Request.QueryString[\"page\"];\n+ Response.Write(page);"));
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(CreateResult());

        var protocolRecorder = CreateProtocolRecorder();
        var defaultChatClient = Substitute.For<IChatClient>();
        defaultChatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis")));

        var prefilterClient = Substitute.For<IChatClient>();
        var prefilterModel = AiConnectionTestFactory.CreateChatModel("gpt-4.1-nano");
        var prefilterBinding = AiConnectionTestFactory.CreateBinding(AiPurpose.ProRVPrefilter, prefilterModel);
        var prefilterConnection = AiConnectionTestFactory.CreateConnection(job.ClientId, [prefilterModel], [prefilterBinding]);
        var prefilterRuntime = new ResolvedAiChatRuntime(
            prefilterConnection,
            prefilterModel,
            prefilterBinding,
            prefilterClient,
            new AgentReviewRuntimeCapabilities(false, false, false, false));
        var aiRuntimeResolver = Substitute.For<IAiRuntimeResolver>();
        aiRuntimeResolver.ResolveChatRuntimeAsync(job.ClientId, AiPurpose.ProRVPrefilter, Arg.Any<CancellationToken>())
            .Returns(prefilterRuntime);

        var proRvPrefilter = Substitute.For<IProRVPrefilter>();
        proRvPrefilter.RankRelevantItemsAsync(
                Arg.Any<ProRVPrefilterRequest>(),
                Arg.Any<IChatClient>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new ProRVPrefilterResult(
                    ProRVPrefilterStatus.Success,
                    "src/Web/ErrorPage.cs",
                    "csharp",
                    [
                        new ProRVRelevantItem(
                            "cs/web/xss",
                            "Cross-site scripting",
                            "Writing request input directly to a response path.",
                            "What to look for:\n- Direct writes of request input to a response sink.",
                            "Request input is written to the response path.",
                            96,
                            "error",
                            "high",
                            ["security", "xss"]),
                    ],
                    "{\"ranked_checks\":[]}",
                    inputTokens: 12,
                    outputTokens: 4));

        var jobRepo = CreateJobRepo();
        jobRepo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        var sut = CreateOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepo,
            defaultChatClient,
            aiRuntimeResolver: aiRuntimeResolver,
            proRvPrefilter: proRvPrefilter);

        await sut.ReviewAsync(job, pr, CreateContext(), CancellationToken.None);

        await proRvPrefilter.Received(1)
            .RankRelevantItemsAsync(
                Arg.Is<ProRVPrefilterRequest>(request => request.FilePath == "src/Web/ErrorPage.cs" && request.Language == "csharp" && request.MaxResults == 4),
                prefilterClient,
                Arg.Is<ChatOptions?>(options => options != null && options.ModelId == "gpt-4.1-nano"),
                Arg.Any<CancellationToken>());

        await aiCore.Received(1)
            .ReviewAsync(
                Arg.Any<PullRequest>(),
                Arg.Is<ReviewSystemContext>(context =>
                    context.PerFileHint != null &&
                    context.PerFileHint.FocusedReviewGuidance.Count == 1 &&
                    context.PerFileHint.FocusedReviewGuidance[0].Id == "cs/web/xss"),
                Arg.Any<CancellationToken>());

        await protocolRecorder.Received()
            .RecordAiCallAsync(
                Arg.Any<Guid>(),
                0,
                12,
                4,
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(),
                ReviewProtocolEventNames.ProRVPrefilterAiCall,
                Arg.Any<string?>());
    }

    [Fact]
    public async Task ReviewAsync_PythonFile_MapsToPythonProRvLanguage()
    {
        var job = CreateJob();
        job.SelectReviewStrategy(
            ReviewStrategy.FileByFile,
            ReviewStrategySelectionSource.ClientDefault,
            ReviewComparisonMode.Single,
            ReviewPublicationMode.Publish,
            null,
            ReviewPipelineProfileProvider.FileByFileBaselineProfileId);
        var pr = CreatePr(
            new ChangedFile(
                "src/service.py",
                ChangeType.Edit,
                "content",
                "+def handle(path):\n+    return open(path).read()"));
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(CreateResult());

        var protocolRecorder = CreateProtocolRecorder();
        var defaultChatClient = Substitute.For<IChatClient>();
        defaultChatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis")));

        var prefilterClient = Substitute.For<IChatClient>();
        var prefilterModel = AiConnectionTestFactory.CreateChatModel("gpt-4.1-nano");
        var prefilterBinding = AiConnectionTestFactory.CreateBinding(AiPurpose.ProRVPrefilter, prefilterModel);
        var prefilterConnection = AiConnectionTestFactory.CreateConnection(job.ClientId, [prefilterModel], [prefilterBinding]);
        var prefilterRuntime = new ResolvedAiChatRuntime(
            prefilterConnection,
            prefilterModel,
            prefilterBinding,
            prefilterClient,
            new AgentReviewRuntimeCapabilities(false, false, false, false));
        var aiRuntimeResolver = Substitute.For<IAiRuntimeResolver>();
        aiRuntimeResolver.ResolveChatRuntimeAsync(job.ClientId, AiPurpose.ProRVPrefilter, Arg.Any<CancellationToken>())
            .Returns(prefilterRuntime);

        var proRvPrefilter = Substitute.For<IProRVPrefilter>();
        proRvPrefilter.RankRelevantItemsAsync(
                Arg.Any<ProRVPrefilterRequest>(),
                Arg.Any<IChatClient>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new ProRVPrefilterResult(
                    ProRVPrefilterStatus.Success,
                    "src/service.py",
                    "python",
                    [],
                    "{\"ranked_checks\":[]}",
                    inputTokens: 6,
                    outputTokens: 2));

        var jobRepo = CreateJobRepo();
        jobRepo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        var sut = CreateOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepo,
            defaultChatClient,
            aiRuntimeResolver: aiRuntimeResolver,
            proRvPrefilter: proRvPrefilter);

        await sut.ReviewAsync(job, pr, CreateContext(), CancellationToken.None);

        await proRvPrefilter.Received(1)
            .RankRelevantItemsAsync(
                Arg.Is<ProRVPrefilterRequest>(request => request.FilePath == "src/service.py" && request.Language == "python" && request.MaxResults == 4),
                prefilterClient,
                Arg.Is<ChatOptions?>(options => options != null && options.ModelId == "gpt-4.1-nano"),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_FileByFileProfileWithoutProRvDispatchStage_DoesNotInvokeProRvPrefilter()
    {
        var job = CreateJob();
        job.SelectReviewStrategy(
            ReviewStrategy.FileByFile,
            ReviewStrategySelectionSource.ClientDefault,
            ReviewComparisonMode.Single,
            ReviewPublicationMode.Publish,
            null,
            "file-by-file-no-prorv");

        var pr = CreatePr(new ChangedFile("src/Web/ErrorPage.cs", ChangeType.Edit, "content", "+ Response.Write(page);"));
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(CreateResult());

        var profileProvider = Substitute.For<IReviewPipelineProfileProvider>();
        profileProvider.GetProfiles(ReviewStrategy.FileByFile).Returns(
        [
            new ReviewPipelineProfile(
                ReviewPipelineProfileProvider.FileByFileBaselineProfileId,
                "File-by-file baseline",
                ReviewStrategy.FileByFile,
                [FileByFileProRvPrefilterStage.StageIdConstant],
                [
                    FileByFileConfidenceFloorStage.StageIdConstant,
                    FileByFileSpeculativeCommentFilterStage.StageIdConstant,
                    FileByFileInfoCommentStripStage.StageIdConstant,
                    FileByFileVagueSuggestionFilterStage.StageIdConstant,
                ],
                [ReviewPipelineProfileProvider.FinalizeStageFamilyId],
                true),
            new ReviewPipelineProfile(
                "file-by-file-no-prorv",
                "File-by-file without ProRV",
                ReviewStrategy.FileByFile,
                [],
                [
                    FileByFileConfidenceFloorStage.StageIdConstant,
                    FileByFileSpeculativeCommentFilterStage.StageIdConstant,
                    FileByFileInfoCommentStripStage.StageIdConstant,
                    FileByFileVagueSuggestionFilterStage.StageIdConstant,
                ],
                [ReviewPipelineProfileProvider.FinalizeStageFamilyId],
                false),
        ]);

        var protocolRecorder = CreateProtocolRecorder();
        var defaultChatClient = Substitute.For<IChatClient>();
        defaultChatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis")));

        var proRvPrefilter = Substitute.For<IProRVPrefilter>();
        var jobRepo = CreateJobRepo();
        jobRepo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        var sut = CreateOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepo,
            defaultChatClient,
            pipelineProfileProvider: profileProvider,
            proRvPrefilter: proRvPrefilter);

        await sut.ReviewAsync(job, pr, CreateContext(), CancellationToken.None);

        await proRvPrefilter.DidNotReceive()
            .RankRelevantItemsAsync(
                Arg.Any<ProRVPrefilterRequest>(),
                Arg.Any<IChatClient>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());

        await aiCore.Received(1)
            .ReviewAsync(
                Arg.Any<PullRequest>(),
                Arg.Is<ReviewSystemContext>(context =>
                    context.PerFileHint != null &&
                    context.PerFileHint.FocusedReviewGuidance.Count == 0),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public void GetProfiles_FileByFile_PublishesRecallUpliftCatalogWithBalancedBaseline()
    {
        var sut = new ReviewPipelineProfileProvider();

        var profiles = sut.GetProfiles(ReviewStrategy.FileByFile);

        var legacyBaseline = Assert.Single(profiles, profile => profile.ProfileId == ReviewPipelineProfileProvider.FileByFileBaselineProfileId);
        var calm = Assert.Single(profiles, profile => profile.ProfileId == ReviewPipelineProfileProvider.FileByFileCalmProfileId);
        var balanced = Assert.Single(profiles, profile => profile.ProfileId == ReviewPipelineProfileProvider.FileByFileBalancedProfileId);
        var assertive = Assert.Single(profiles, profile => profile.ProfileId == ReviewPipelineProfileProvider.FileByFileAssertiveProfileId);

        var expectedDispatchStages = new[]
        {
            FileByFileContextPrefetchStage.StageIdConstant,
            FileByFileRiskMarkerStage.StageIdConstant,
            FileByFileProRvPrefilterStage.StageIdConstant,
        };

        Assert.False(legacyBaseline.IsBaseline);
        Assert.Equal(expectedDispatchStages, legacyBaseline.DispatchStageIds);
        Assert.Equal(
            [
                FileByFileConfidenceFloorStage.StageIdConstant,
                FileByFileSpeculativeCommentFilterStage.StageIdConstant,
                FileByFileInfoCommentStripStage.StageIdConstant,
                FileByFileVagueSuggestionFilterStage.StageIdConstant,
            ],
            legacyBaseline.PerFileStageIds);

        Assert.False(calm.IsBaseline);
        Assert.Equal(expectedDispatchStages, calm.DispatchStageIds);
        Assert.Equal(
            [
                FileByFileConfidenceFloorStage.StageIdConstant,
                FileByFileSpeculativeCommentFilterStage.StageIdConstant,
                FileByFileInfoCommentStripStage.StageIdConstant,
                FileByFileVagueSuggestionFilterStage.StageIdConstant,
            ],
            calm.PerFileStageIds);

        Assert.True(balanced.IsBaseline);
        Assert.Equal(expectedDispatchStages, balanced.DispatchStageIds);
        Assert.Equal(
            [
                FileByFileConfidenceFloorStage.StageIdConstant,
                FileByFileInfoCommentStripStage.StageIdConstant,
                FileByFileSelfReflectionRankingStage.StageIdConstant,
            ],
            balanced.PerFileStageIds);

        Assert.False(assertive.IsBaseline);
        Assert.Equal(expectedDispatchStages, assertive.DispatchStageIds);
        Assert.Equal(
            [
                FileByFileInfoCommentStripStage.StageIdConstant,
                FileByFileSelfReflectionRankingStage.StageIdConstant,
            ],
            assertive.PerFileStageIds);
    }

    [Fact]
    public async Task ReviewAsync_WithoutExplicitProfile_UsesBalancedBaselineProfileWithRecallDispatchStages()
    {
        var job = CreateJob();
        job.SelectReviewStrategy(
            ReviewStrategy.FileByFile,
            ReviewStrategySelectionSource.ClientDefault,
            ReviewComparisonMode.Single,
            ReviewPublicationMode.Publish,
            null);

        var pr = CreatePr(CreateFile("src/Foo.cs"));
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(CreateResult());

        var protocolRecorder = CreateProtocolRecorder();
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis")));

        var jobRepo = CreateJobRepo();
        jobRepo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        var sut = CreateOrchestrator(aiCore, protocolRecorder, jobRepo, chatClient);

        await sut.ReviewAsync(job, pr, CreateContext(), CancellationToken.None);

        await protocolRecorder.Received(1)
            .RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(),
                ReviewProtocolEventNames.ReviewPipelineProfileApplied,
                Arg.Is<string?>(details =>
                    details != null
                    && details.Contains(ReviewPipelineProfileProvider.FileByFileBalancedProfileId, StringComparison.Ordinal)
                    && details.Contains("src/Foo.cs", StringComparison.Ordinal)),
                Arg.Is<string?>(output =>
                    output != null
                    && output.Contains(FileByFileContextPrefetchStage.StageIdConstant, StringComparison.Ordinal)
                    && output.Contains(FileByFileRiskMarkerStage.StageIdConstant, StringComparison.Ordinal)
                    && output.Contains(FileByFileProRvPrefilterStage.StageIdConstant, StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WithBalancedProfile_RecordsReviewProfileSelectionSourceAndProfileId()
    {
        var job = CreateJob();
        job.SelectReviewStrategy(
            ReviewStrategy.FileByFile,
            ReviewStrategySelectionSource.ClientDefault,
            ReviewComparisonMode.Single,
            ReviewPublicationMode.Publish,
            null,
            ReviewPipelineProfileProvider.FileByFileBalancedProfileId);

        var pr = CreatePr(CreateFile("src/Foo.cs"));
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(CreateResult());

        var protocolRecorder = CreateProtocolRecorder();
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis")));

        var jobRepo = CreateJobRepo();
        jobRepo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        var sut = CreateOrchestrator(aiCore, protocolRecorder, jobRepo, chatClient);
        var context = CreateContext();
        context.ActiveProtocolId = Guid.NewGuid();
        context.ProtocolRecorder = protocolRecorder;

        await sut.ReviewAsync(job, pr, context, CancellationToken.None);

        await protocolRecorder.Received(1)
            .RecordReviewStrategyEventAsync(
                context.ActiveProtocolId.Value,
                ReviewProtocolEventNames.ReviewProfileSelected,
                Arg.Is<string?>(details =>
                    details != null
                    && details.Contains("\"strategy\":\"FileByFile\"", StringComparison.Ordinal)
                    && details.Contains("\"selectionSource\":\"ClientDefault\"", StringComparison.Ordinal)),
                Arg.Is<string?>(output =>
                    output != null
                    && output.Contains(ReviewPipelineProfileProvider.FileByFileBalancedProfileId, StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WithBalancedProfile_KeepsOnlyTopRankedPerFileComments()
    {
        var job = CreateJob();
        job.SelectReviewStrategy(
            ReviewStrategy.FileByFile,
            ReviewStrategySelectionSource.ClientDefault,
            ReviewComparisonMode.Single,
            ReviewPublicationMode.Publish,
            null,
            ReviewPipelineProfileProvider.FileByFileBalancedProfileId);

        var pr = CreatePr(CreateFile("src/Foo.cs"));
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewResult(
                    "file summary",
                    [
                        new ReviewComment("src/Foo.cs", 30, CommentSeverity.Warning, "Consider renaming helper for readability."),
                        new ReviewComment("src/Foo.cs", 18, CommentSeverity.Error, "Authorization check is missing before token-backed action."),
                        new ReviewComment("src/Foo.cs", 40, CommentSeverity.Warning, "Maybe simplify this expression."),
                    ]));

        var storedResults = new List<ReviewFileResult>();
        var repo = Substitute.For<IJobRepository>();
        repo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repo.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                storedResults.Add(ci.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repo.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis")));

        var sut = CreateOrchestrator(
            aiCore,
            CreateProtocolRecorder(),
            repo,
            chatClient,
            CreateOptions(options =>
            {
                options.ImportanceRankingKeepTopN = 1;
                options.ImportanceRankingMinScore = 5;
            }));

        await sut.ReviewAsync(job, pr, CreateContext(), CancellationToken.None);

        var completedResult = Assert.Single(storedResults, result => result.IsComplete);
        var keptComment = Assert.Single(completedResult.Comments!);
        Assert.Equal("Authorization check is missing before token-backed action.", keptComment.Message);
    }

    [Fact]
    public async Task ReviewAsync_ContextPrefetchStage_AddsBoundedEvidenceToPerFileHintBeforeAiReview()
    {
        var job = CreateJob();
        var file = new ChangedFile(
            "src/Foo.cs",
            ChangeType.Edit,
            "public sealed class FooService\n{\n    public string Create(string value)\n    {\n        return value.Trim();\n    }\n}",
            "+ public string Create(string value)");
        var pr = CreatePr(file);

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(
                Arg.Any<PullRequest>(),
                Arg.Is<ReviewSystemContext>(reviewContext =>
                    reviewContext.PerFileHint != null
                    && reviewContext.PerFileHint.PrefetchedContextEvidence.Count == 2
                    && reviewContext.PerFileHint.PrefetchedContextEvidence.Any(item =>
                        string.Equals(item.Kind, "surrounding_definition", StringComparison.Ordinal)
                        && string.Equals(item.SourceId, "src/Foo.cs", StringComparison.Ordinal))
                    && reviewContext.PerFileHint.PrefetchedContextEvidence.Any(item =>
                        string.Equals(item.Kind, "supported_caller_site", StringComparison.Ordinal)
                        && string.Equals(item.SourceId, "src/Bar.cs:L27", StringComparison.Ordinal)
                        && item.Truncated)),
                Arg.Any<CancellationToken>())
            .Returns(CreateResult());

        var reviewTools = Substitute.For<IReviewContextTools>();
        reviewTools.SearchCodeAsync(
                Arg.Is<CodeSearchRequest>(request =>
                    string.Equals(request.SearchMode, CodeSearchModes.RelatedSymbol, StringComparison.Ordinal)
                    && string.Equals(request.BranchSide, RepositorySearchBranchSides.Source, StringComparison.Ordinal)
                    && string.Equals(request.PathScope, RepositorySearchPathScopes.Repository, StringComparison.Ordinal)),
                Arg.Any<CancellationToken>())
            .Returns(
                new CodeSearchResult(
                    RepositorySearchStatuses.Success,
                    RepositorySearchBranchSides.Source,
                    RepositorySearchPathScopes.Repository,
                    CodeSearchModes.RelatedSymbol,
                    null,
                    [new CodeSearchMatch("src/Bar.cs", 27, new string('x', 80), "csharp", 1, false)],
                    [],
                    false));

        var protocolRecorder = CreateProtocolRecorder();
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis")));

        var jobRepo = CreateJobRepo();
        jobRepo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        var sut = CreateOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepo,
            chatClient,
            CreateOptions(options =>
            {
                options.MaxPrefetchCallerSites = 1;
                options.MaxPrefetchRegionChars = 40;
            }));

        var context = new ReviewSystemContext(null, [], reviewTools);

        await sut.ReviewAsync(job, pr, context, CancellationToken.None);

        await aiCore.Received(1)
            .ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_ContextPrefetchStage_RecordsContextPrefetchAppliedEvent()
    {
        var job = CreateJob();
        var file = new ChangedFile(
            "src/Foo.cs",
            ChangeType.Edit,
            "public sealed class FooService\n{\n    public string Create(string value)\n    {\n        return value.Trim();\n    }\n}",
            "+ public string Create(string value)");
        var pr = CreatePr(file);

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(CreateResult());

        var reviewTools = Substitute.For<IReviewContextTools>();
        reviewTools.SearchCodeAsync(Arg.Any<CodeSearchRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                new CodeSearchResult(
                    RepositorySearchStatuses.Success,
                    RepositorySearchBranchSides.Source,
                    RepositorySearchPathScopes.Repository,
                    CodeSearchModes.RelatedSymbol,
                    null,
                    [new CodeSearchMatch("src/Bar.cs", 14, "FooService.Create(oldArg);", "csharp", 1, false)],
                    [],
                    false));

        var protocolRecorder = CreateProtocolRecorder();
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis")));

        var jobRepo = CreateJobRepo();
        jobRepo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        var sut = CreateOrchestrator(aiCore, protocolRecorder, jobRepo, chatClient);

        var context = new ReviewSystemContext(null, [], reviewTools);

        await sut.ReviewAsync(job, pr, context, CancellationToken.None);

        await protocolRecorder.Received(1)
            .RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(),
                ReviewProtocolEventNames.ContextPrefetchApplied,
                Arg.Is<string?>(details =>
                    details != null
                    && details.Contains("src/Foo.cs", StringComparison.Ordinal)
                    && details.Contains("evidenceCount", StringComparison.Ordinal)),
                Arg.Is<string?>(output =>
                    output != null
                    && output.Contains("surrounding_definition", StringComparison.Ordinal)
                    && output.Contains("supported_caller_site", StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("+ var token = Request.Headers[\"Authorization\"];", true, false)]
    [InlineData("+ await Task.WhenAll(tasks);", false, true)]
    [InlineData("+ foreach (async item in items) { }", false, true)]
    [InlineData("+ var value = 42;", false, false)]
    public async Task ReviewAsync_RiskMarkerStage_FlagsExpectedSecurityAndConcurrencyMarkers(
        string diff,
        bool expectedSecurity,
        bool expectedConcurrency)
    {
        var job = CreateJob();
        var pr = CreatePr(new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", diff));
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(
                Arg.Any<PullRequest>(),
                Arg.Is<ReviewSystemContext>(context =>
                    context.PerFileHint != null
                    && context.PerFileHint.RiskMarkers.HasSecurityMarkers == expectedSecurity
                    && context.PerFileHint.RiskMarkers.HasConcurrencyMarkers == expectedConcurrency),
                Arg.Any<CancellationToken>())
            .Returns(CreateResult());

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis")));

        var jobRepo = CreateJobRepo();
        jobRepo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        var sut = CreateOrchestrator(aiCore, CreateProtocolRecorder(), jobRepo, chatClient);

        await sut.ReviewAsync(job, pr, CreateContext(), CancellationToken.None);

        await aiCore.Received(1).ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WithLateAugmentationAndRiskMarkers_RecordsSecuritySpecialistPassEvent()
    {
        var job = CreateJob();
        var pr = CreatePr(new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", "+ var token = Request.Headers[\"Authorization\"];"));
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewResult("baseline summary", []),
                new ReviewResult("specialist summary", [new ReviewComment("src/Foo.cs", 12, CommentSeverity.Warning, "Token handling bypasses validation.")]));

        var protocolRecorder = CreateProtocolRecorder();
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis")));

        var storedResults = new List<ReviewFileResult>();
        var repo = Substitute.For<IJobRepository>();
        repo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repo.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                storedResults.Add(ci.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repo.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var sut = CreateOrchestrator(aiCore, protocolRecorder, repo, chatClient);
        var context = CreateContext();
        context.AugmentationMode = ReviewAugmentationMode.LateAugmentation;
        context.EnableProRV = true;
        context.ActiveProtocolId = Guid.NewGuid();
        context.ProtocolRecorder = protocolRecorder;

        await sut.ReviewAsync(job, pr, context, CancellationToken.None);

        await protocolRecorder.Received(1)
            .RecordReviewStrategyEventAsync(
                Arg.Any<Guid>(),
                ReviewProtocolEventNames.SecuritySpecialistPassRan,
                Arg.Is<string?>(details => details != null && details.Contains("security.auth-token", StringComparison.Ordinal)),
                Arg.Is<string?>(output => output != null && output.Contains("addedFindings", StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WithProRvDisabledInContext_DoesNotInvokeProRvPrefilter()
    {
        var job = CreateJob();
        var pr = CreatePr(new ChangedFile("src/service.py", ChangeType.Edit, "content", "+ return render(title)"));
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(CreateResult());

        var protocolRecorder = CreateProtocolRecorder();
        var defaultChatClient = Substitute.For<IChatClient>();
        defaultChatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis")));

        var proRvPrefilter = Substitute.For<IProRVPrefilter>();
        var jobRepo = CreateJobRepo();
        jobRepo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        var sut = CreateOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepo,
            defaultChatClient,
            CreateOptions(options => options.EnableHighRiskEscalation = false),
            proRvPrefilter: proRvPrefilter);

        var context = CreateContext();
        context.EnableProRV = false;

        await sut.ReviewAsync(job, pr, context, CancellationToken.None);

        await proRvPrefilter.DidNotReceive()
            .RankRelevantItemsAsync(
                Arg.Any<ProRVPrefilterRequest>(),
                Arg.Any<IChatClient>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());

        await aiCore.Received(1)
            .ReviewAsync(
                Arg.Any<PullRequest>(),
                Arg.Is<ReviewSystemContext>(reviewContext =>
                    !reviewContext.EnableProRV &&
                    reviewContext.PerFileHint != null &&
                    reviewContext.PerFileHint.FocusedReviewGuidance.Count == 0),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WithLateAugmentationMode_UsesProRvOnlyForAugmentationPass()
    {
        var job = CreateJob();
        var pr = CreatePr(new ChangedFile("src/service.py", ChangeType.Edit, "content", "+ var token = Request.Headers[\"Authorization\"]"));
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(CreateResult());

        var protocolRecorder = CreateProtocolRecorder();
        var defaultChatClient = Substitute.For<IChatClient>();
        defaultChatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis")));

        var proRvPrefilter = Substitute.For<IProRVPrefilter>();
        var jobRepo = CreateJobRepo();
        jobRepo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        var sut = CreateOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepo,
            defaultChatClient,
            CreateOptions(options => options.EnableHighRiskEscalation = false),
            proRvPrefilter: proRvPrefilter);

        var context = CreateContext();
        context.AugmentationMode = ReviewAugmentationMode.LateAugmentation;

        await sut.ReviewAsync(job, pr, context, CancellationToken.None);

        await proRvPrefilter.Received(1)
            .RankRelevantItemsAsync(
                Arg.Any<ProRVPrefilterRequest>(),
                Arg.Any<IChatClient>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());

        await aiCore.Received(1)
            .ReviewAsync(
                Arg.Any<PullRequest>(),
                Arg.Is<ReviewSystemContext>(reviewContext =>
                    !reviewContext.EnableProRV &&
                    reviewContext.PassKind == ReviewPassKind.Baseline &&
                    reviewContext.AugmentationMode == ReviewAugmentationMode.LateAugmentation &&
                    reviewContext.PerFileHint != null &&
                    reviewContext.PerFileHint.FocusedReviewGuidance.Count == 0),
                Arg.Any<CancellationToken>());

        await aiCore.Received(1)
            .ReviewAsync(
                Arg.Any<PullRequest>(),
                Arg.Is<ReviewSystemContext>(reviewContext =>
                    reviewContext.EnableProRV &&
                    reviewContext.PassKind == ReviewPassKind.ProRVAugmentation &&
                    reviewContext.AugmentationMode == ReviewAugmentationMode.LateAugmentation),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WithLateAugmentationMode_WithoutRiskMarkers_SkipsAugmentationPass()
    {
        var job = CreateJob();
        var pr = CreatePr(new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", "+ var value = 42;"));
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("baseline summary", [new ReviewComment("src/Foo.cs", 12, CommentSeverity.Warning, "Baseline issue")]));

        var protocolRecorder = CreateProtocolRecorder();
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis")));

        var proRvPrefilter = Substitute.For<IProRVPrefilter>();
        var storedResults = new List<ReviewFileResult>();
        var repo = Substitute.For<IJobRepository>();
        repo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repo.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                storedResults.Add(ci.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repo.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var sut = CreateOrchestrator(
            aiCore,
            protocolRecorder,
            repo,
            chatClient,
            proRvPrefilter: proRvPrefilter);

        var context = CreateContext();
        context.AugmentationMode = ReviewAugmentationMode.LateAugmentation;
        context.EnableProRV = true;

        await sut.ReviewAsync(job, pr, context, CancellationToken.None);

        await proRvPrefilter.DidNotReceive()
            .RankRelevantItemsAsync(
                Arg.Any<ProRVPrefilterRequest>(),
                Arg.Any<IChatClient>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>());

        await aiCore.Received(1)
            .ReviewAsync(
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>());

        await aiCore.DidNotReceive()
            .ReviewAsync(
                Arg.Any<PullRequest>(),
                Arg.Is<ReviewSystemContext>(reviewContext =>
                    reviewContext.PassKind == ReviewPassKind.ProRVAugmentation &&
                    reviewContext.EnableProRV),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WithLateAugmentationMode_RunsCurrentFilePassAsBaseline()
    {
        var job = CreateJob();
        var pr = CreatePr(CreateFile("src/Foo.cs"));
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("file summary", [new ReviewComment("src/Foo.cs", 12, CommentSeverity.Warning, "Baseline issue")]));

        var storedResults = new List<ReviewFileResult>();
        var repo = Substitute.For<IJobRepository>();
        repo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repo.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                storedResults.Add(ci.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repo.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis")));

        var sut = CreateOrchestrator(aiCore, CreateProtocolRecorder(), repo, chatClient);
        var context = CreateContext();
        context.AugmentationMode = ReviewAugmentationMode.LateAugmentation;
        context.EnableProRV = true;

        await sut.ReviewAsync(job, pr, context, CancellationToken.None);

        await aiCore.Received(1)
            .ReviewAsync(
                Arg.Any<PullRequest>(),
                Arg.Is<ReviewSystemContext>(reviewContext =>
                    !reviewContext.EnableProRV &&
                    reviewContext.AugmentationMode == ReviewAugmentationMode.LateAugmentation),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public void CandidateFindingFactory_Build_InLateAugmentationModePreservesBaselineOnlyProvenance()
    {
        var fileResult = new ReviewFileResult(Guid.NewGuid(), "src/Foo.cs");
        fileResult.MarkCompleted("summary", [new ReviewComment("src/Foo.cs", 12, CommentSeverity.Warning, "Baseline issue")]);

        var finding = Assert.Single(new CandidateFindingFactory(null).Build([fileResult]));

        Assert.Equal(ReviewPassKind.Baseline, finding.Provenance.ReviewPassKind);
        Assert.Equal(FindingProvenanceKind.BaselineOnly, finding.Provenance.FindingProvenanceKind);
    }

    [Fact]
    public async Task ReviewAsync_WithLateAugmentationMode_RunsSecondProRvAugmentationPass()
    {
        var job = CreateJob();
        var pr = CreatePr(new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", "+ var token = Request.Headers[\"Authorization\"]"));
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewResult("baseline summary", [new ReviewComment("src/Foo.cs", 12, CommentSeverity.Warning, "Baseline issue")]),
                new ReviewResult("augmentation summary", [new ReviewComment("src/Foo.cs", 20, CommentSeverity.Warning, "Augmentation issue")]));

        var storedResults = new List<ReviewFileResult>();
        var repo = Substitute.For<IJobRepository>();
        repo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repo.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                storedResults.Add(ci.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repo.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis")));

        var sut = CreateOrchestrator(aiCore, CreateProtocolRecorder(), repo, chatClient);
        var context = CreateContext();
        context.AugmentationMode = ReviewAugmentationMode.LateAugmentation;
        context.EnableProRV = true;

        await sut.ReviewAsync(job, pr, context, CancellationToken.None);

        await aiCore.Received(2)
            .ReviewAsync(
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>());

        await aiCore.Received(1)
            .ReviewAsync(
                Arg.Any<PullRequest>(),
                Arg.Is<ReviewSystemContext>(reviewContext =>
                    reviewContext.PassKind == ReviewPassKind.ProRVAugmentation &&
                    reviewContext.EnableProRV &&
                    reviewContext.AugmentationMode == ReviewAugmentationMode.LateAugmentation),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WithLateAugmentationMode_RecordsBaselineAugmentationAndMergeProtocolEvents()
    {
        var job = CreateJob();
        var pr = CreatePr(new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", "+ var token = Request.Headers[\"Authorization\"]"));
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewResult("baseline summary", [new ReviewComment("src/Foo.cs", 12, CommentSeverity.Warning, "Baseline issue")]),
                new ReviewResult("augmentation summary", [new ReviewComment("src/Foo.cs", 20, CommentSeverity.Warning, "Augmentation issue")]));

        var storedResults = new List<ReviewFileResult>();
        var repo = Substitute.For<IJobRepository>();
        repo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repo.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                storedResults.Add(ci.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repo.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var protocolRecorder = CreateProtocolRecorder();
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis")));

        var sut = CreateOrchestrator(aiCore, protocolRecorder, repo, chatClient);
        var context = CreateContext();
        context.AugmentationMode = ReviewAugmentationMode.LateAugmentation;
        context.EnableProRV = true;
        context.ActiveProtocolId = Guid.NewGuid();
        context.ProtocolRecorder = protocolRecorder;

        await sut.ReviewAsync(job, pr, context, CancellationToken.None);

        Received.InOrder(() =>
        {
            protocolRecorder.RecordReviewStrategyEventAsync(
                context.ActiveProtocolId.Value,
                ReviewProtocolEventNames.LateSteeringBaselinePassCompleted,
                Arg.Is<string?>(details => details != null && details.Contains("\"passKind\":\"Baseline\"", StringComparison.Ordinal)),
                Arg.Any<string?>(),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());

            protocolRecorder.RecordReviewStrategyEventAsync(
                context.ActiveProtocolId.Value,
                ReviewProtocolEventNames.LateSteeringAugmentationPassCompleted,
                Arg.Is<string?>(details => details != null && details.Contains("\"passKind\":\"ProRVAugmentation\"", StringComparison.Ordinal)),
                Arg.Any<string?>(),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());

            protocolRecorder.RecordReviewStrategyEventAsync(
                context.ActiveProtocolId.Value,
                ReviewProtocolEventNames.LateSteeringMergeCompleted,
                Arg.Is<string?>(details =>
                    details != null &&
                    details.Contains("\"baselineCandidateCount\":1", StringComparison.Ordinal) &&
                    details.Contains("\"proRvCandidateCount\":1", StringComparison.Ordinal) &&
                    details.Contains("\"mergedCandidateCount\":2", StringComparison.Ordinal)),
                Arg.Is<string?>(output =>
                    output != null &&
                    output.Contains("\"baselineOnlyCount\":1", StringComparison.Ordinal) &&
                    output.Contains("\"proRvOnlyCount\":1", StringComparison.Ordinal) &&
                    output.Contains("\"bothCount\":0", StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task ReviewAsync_WithLateAugmentationMode_AndZeroFindingHighRiskFile_RunsDedicatedEscalationPass()
    {
        var capturedContexts = new List<ReviewSystemContext>();
        var job = CreateJob();
        var pr = CreatePr(new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", "+ var token = Request.Headers[\"Authorization\"]"));
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(
                Arg.Any<PullRequest>(),
                Arg.Do<ReviewSystemContext>(context => capturedContexts.Add(context)),
                Arg.Any<CancellationToken>())
            .Returns(
                new ReviewResult("baseline summary", []),
                new ReviewResult("specialist summary", []),
                new ReviewResult(
                    "escalation summary", [new ReviewComment("src/Foo.cs", 20, CommentSeverity.Warning, "Escalation uncovered missing token validation.")]));

        var storedResults = new List<ReviewFileResult>();
        var repo = Substitute.For<IJobRepository>();
        repo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repo.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                storedResults.Add(ci.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repo.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis")));

        var sut = CreateOrchestrator(aiCore, CreateProtocolRecorder(), repo, chatClient);
        var context = CreateContext();
        context.AugmentationMode = ReviewAugmentationMode.LateAugmentation;
        context.EnableProRV = true;

        await sut.ReviewAsync(job, pr, context, CancellationToken.None);

        await aiCore.Received(3)
            .ReviewAsync(
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>());

        Assert.Contains(
            capturedContexts,
            reviewContext =>
                reviewContext.PassKind == ReviewPassKind.ProRVAugmentation &&
                reviewContext.PerFileHint?.FocusedReviewGuidance.Any(item => item.Id == "high-risk.zero-finding-second-look") == true);
    }

    [Fact]
    public async Task ReviewAsync_WithLateAugmentationMode_AndSilentHighRiskEscalation_RecordsAuditEvents()
    {
        var job = CreateJob();
        var pr = CreatePr(new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", "+ var token = Request.Headers[\"Authorization\"]"));
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(
                new ReviewResult("baseline summary", []),
                new ReviewResult("specialist summary", []),
                new ReviewResult("escalation summary", []));

        var storedResults = new List<ReviewFileResult>();
        var repo = Substitute.For<IJobRepository>();
        repo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repo.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                storedResults.Add(ci.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repo.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var protocolRecorder = CreateProtocolRecorder();
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis")));

        var sut = CreateOrchestrator(aiCore, protocolRecorder, repo, chatClient);
        var context = CreateContext();
        context.AugmentationMode = ReviewAugmentationMode.LateAugmentation;
        context.EnableProRV = true;
        context.ActiveProtocolId = Guid.NewGuid();
        context.ProtocolRecorder = protocolRecorder;

        await sut.ReviewAsync(job, pr, context, CancellationToken.None);

        await protocolRecorder.Received(1)
            .RecordReviewStrategyEventAsync(
                context.ActiveProtocolId.Value,
                ReviewProtocolEventNames.HighRiskEscalationRan,
                Arg.Is<string?>(details => details != null && details.Contains("src/Foo.cs", StringComparison.Ordinal)),
                Arg.Is<string?>(output => output != null && output.Contains("\"addedFindings\":0", StringComparison.Ordinal)),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());

        await protocolRecorder.Received(1)
            .RecordReviewStrategyEventAsync(
                context.ActiveProtocolId.Value,
                ReviewProtocolEventNames.ZeroCandidateHighRisk,
                Arg.Is<string?>(details => details != null && details.Contains("security.auth-token", StringComparison.Ordinal)),
                Arg.Any<string?>(),
                Arg.Is<string?>(error => error == null),
                Arg.Any<CancellationToken>());
    }

    // ─── T049: synthesis JSON cross_cutting_concerns ──────────────────────────────

    [Fact]
    public async Task ReviewAsync_SynthesisReturnsCrossCuttingConcerns_AddsProLevelComments()
    {
        const string synthesisJson =
            """{"summary":"Overall good PR.","cross_cutting_concerns":[{"message":"Missing DI registration in multiple files","severity":"error"}]}""";

        var job = CreateJob();
        var pr = CreatePr(CreateFile("src/Foo.cs"));
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(CreateResult("file summary"));

        var chatClient = Substitute.For<IChatClient>();
        // synthesis call returns JSON with cross_cutting_concerns
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, synthesisJson)));

        var jobRepo = CreateJobRepo();
        jobRepo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        var sut = CreateOrchestrator(aiCore, CreateProtocolRecorder(), jobRepo, chatClient);

        // Act
        var result = await sut.ReviewAsync(job, pr, CreateContext(), CancellationToken.None);

        // Assert: one PR-level comment (FilePath=null) for the cross-cutting concern
        var prLevelComments = result.Comments?.Where(c => c.FilePath is null).ToList();
        Assert.NotNull(prLevelComments);
        Assert.Single(prLevelComments);
        Assert.Contains("Missing DI registration in multiple files", prLevelComments[0].Message);
        Assert.Equal(CommentSeverity.Error, prLevelComments[0].Severity);
    }

    [Fact]
    public async Task ReviewAsync_SynthesisJsonNoCrossCuttingConcerns_NoProLevelComments()
    {
        const string synthesisJson = """{"summary":"Overall good PR.","cross_cutting_concerns":[]}""";

        var job = CreateJob();
        var pr = CreatePr(CreateFile("src/Foo.cs"));
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(CreateResult());

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, synthesisJson)));

        var jobRepo = CreateJobRepo();
        jobRepo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>()).Returns(job);

        var sut = CreateOrchestrator(aiCore, CreateProtocolRecorder(), jobRepo, chatClient);

        var result = await sut.ReviewAsync(job, pr, CreateContext(), CancellationToken.None);

        var prLevelComments = result.Comments?.Where(c => c.FilePath is null).ToList();
        Assert.True(prLevelComments is null || prLevelComments.Count == 0);
        Assert.Equal("Overall good PR.", result.Summary);
    }

    [Fact]
    public async Task ReviewAsync_SynthesisMalformedJson_RepairsResponseAndUsesParsedSummary()
    {
        const string malformedJson =
            """{"summary":"This mentions v-for="tag in post.tags" without escaping.","cross_cutting_concerns":[]}""";
        const string repairedJson =
            """{"summary":"This mentions v-for=\"tag in post.tags\" without escaping.","cross_cutting_concerns":[]}""";

        var comment = new ReviewComment("src/Foo.cs", 10, CommentSeverity.Warning, "Potential issue");
        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("file summary", new List<ReviewComment> { comment }.AsReadOnly()));

        var job = CreateJob();
        var pr = CreatePr(CreateFile("src/Foo.cs"));

        var storedResults = new List<ReviewFileResult>();
        var repo = Substitute.For<IJobRepository>();
        repo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repo.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                storedResults.Add(ci.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repo.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, malformedJson)),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, repairedJson)));

        var sut = CreateOrchestrator(aiCore, CreateProtocolRecorder(), repo, chatClient);

        var result = await sut.ReviewAsync(job, pr, CreateContext(), CancellationToken.None);

        Assert.Equal("This mentions v-for=\"tag in post.tags\" without escaping.", result.Summary);
        await chatClient.Received(2)
            .GetResponseAsync(Arg.Any<IList<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_WhenProviderManagedContinuationFails_FileReviewFallsBackAndStillCompletes()
    {
        var protocolId = Guid.NewGuid();
        ReviewSystemContext? capturedFileContext = null;

        var providerManagedClient = Substitute.For<IChatClient>();
        providerManagedClient
            .GetResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(
                _ => new ChatResponse(
                [
                    new ChatMessage(
                        ChatRole.Assistant,
                        [new FunctionCallContent("call-1", "get_changed_files")]),
                    new ChatMessage(
                        ChatRole.Tool,
                        [new FunctionResultContent("call-1", "[]")]),
                ])
                {
                    ConversationId = "conv-1",
                    ResponseId = "resp-1",
                },
                _ => throw new InvalidOperationException("provider continuation failed"),
                _ => new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant, """
                                            {"summary":"per-file done","comments":[],"confidence_evaluations":[{"concern":"correctness","confidence":90}],"investigation_complete":true,"loop_complete":true}
                                            """)),
                _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis summary")));

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(
                Arg.Any<PullRequest>(),
                Arg.Do<ReviewSystemContext>(context => capturedFileContext = context),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var pr = callInfo.Arg<PullRequest>();
                var context = callInfo.ArgAt<ReviewSystemContext>(1);
                var runtime = new ToolAwareAiReviewCore(
                    providerManagedClient,
                    DefaultOptions(),
                    Substitute.For<ILogger<ToolAwareAiReviewCore>>());
                return runtime.ReviewAsync(pr, context, callInfo.Arg<CancellationToken>());
            });

        var job = CreateJob();
        var pr = CreatePr(CreateFile("src/Foo.cs"));
        var storedResults = new List<ReviewFileResult>();
        var repo = Substitute.For<IJobRepository>();
        repo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repo.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                storedResults.Add(callInfo.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repo.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var protocolRecorder = CreateProtocolRecorder();
        protocolRecorder.BeginAsync(
                Arg.Any<Guid>(),
                Arg.Any<int>(),
                Arg.Any<string?>(),
                Arg.Any<Guid?>(),
                Arg.Any<AiConnectionModelCategory?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(protocolId);

        var sut = CreateOrchestrator(aiCore, protocolRecorder, repo, providerManagedClient);
        var context = CreateContext();
        context.RuntimeCapabilities = new AgentReviewRuntimeCapabilities(true, true, false, true);

        var result = await sut.ReviewAsync(job, pr, context, CancellationToken.None);

        Assert.Equal("synthesis summary", result.Summary);

        Assert.NotNull(capturedFileContext);
        Assert.NotNull(capturedFileContext!.ReviewSession);
        Assert.Equal(AgentReviewSessionMode.LocalManagedSession, capturedFileContext.ReviewSession!.Mode);
        Assert.Single(capturedFileContext.ReviewSession.Fallbacks);
        Assert.NotNull(capturedFileContext.LoopMetrics);
        Assert.Contains("provider_session_continue_failed", capturedFileContext.LoopMetrics!.FallbacksJson, StringComparison.Ordinal);

        await protocolRecorder.Received()
            .RecordReviewStrategyEventAsync(
                protocolId,
                ReviewProtocolEventNames.ReviewAgentSessionFallback,
                Arg.Is<string?>(json => json != null && json.Contains("provider_session_continue_failed", StringComparison.Ordinal)),
                Arg.Is<string?>(value => value == null),
                Arg.Is<string?>(value => value == null),
                Arg.Any<CancellationToken>());
        await protocolRecorder.Received()
            .SetCompletedAsync(
                protocolId,
                "Completed",
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewAsync_TwoFiles_UsesDistinctPerFileManagedConversationOwnership()
    {
        var capturedFileContexts = new List<ReviewSystemContext>();
        var capturedPullRequests = new List<PullRequest>();
        var providerManagedClient = Substitute.For<IChatClient>();
        providerManagedClient.GetResponseAsync(
                Arg.Any<IList<ChatMessage>>(),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "synthesis summary")));

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(
                Arg.Do<PullRequest>(prArg => capturedPullRequests.Add(prArg)),
                Arg.Do<ReviewSystemContext>(context => capturedFileContexts.Add(context)),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var prArg = callInfo.Arg<PullRequest>();
                var context = callInfo.ArgAt<ReviewSystemContext>(1);
                var filePath = Assert.Single(prArg.ChangedFiles).Path;
                var suffix = filePath.EndsWith("Foo.cs", StringComparison.Ordinal) ? "a" : "b";
                context.ReviewSession = new AgentReviewSession(
                    $"session-{suffix}",
                    AgentReviewSessionMode.ProviderManagedSession,
                    AgentReviewSessionStatus.Completed,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    new SessionContinuationHandle(
                        SessionContinuationHandleType.ProviderSession,
                        $"conv-{suffix}",
                        $"conv-{suffix}",
                        $"resp-{suffix}",
                        DateTimeOffset.UtcNow),
                    [],
                    [],
                    AgentReviewPromptMode.CurrentPromptOnly,
                    $"conv-{suffix}");
                context.LoopMetrics = new ReviewLoopMetrics(
                    0,
                    null,
                    null,
                    90,
                    0,
                    0,
                    1,
                    AgentReviewSessionMode.ProviderManagedSession,
                    null,
                    null,
                    $"conv-{suffix}",
                    $"resp-{suffix}",
                    AgentReviewPromptMode.CurrentPromptOnly);
                return Task.FromResult(new ReviewResult("per-file done", []));
            });

        var job = CreateJob();
        var pr = CreatePr(CreateFile("src/Foo.cs"), CreateFile("src/Bar.cs"));
        var storedResults = new List<ReviewFileResult>();
        var repo = Substitute.For<IJobRepository>();
        repo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repo.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                storedResults.Add(callInfo.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repo.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var sut = CreateOrchestrator(aiCore, CreateProtocolRecorder(), repo, providerManagedClient);
        var context = CreateContext();
        context.RuntimeCapabilities = new AgentReviewRuntimeCapabilities(true, true, false, true);
        context.ReviewSession = new AgentReviewSession(
            "stale-base-session",
            AgentReviewSessionMode.ProviderManagedSession,
            AgentReviewSessionStatus.Active,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            [],
            [],
            AgentReviewPromptMode.CurrentPromptOnly,
            "stale-base-conversation");

        var result = await sut.ReviewAsync(job, pr, context, CancellationToken.None);

        Assert.Equal("synthesis summary", result.Summary);
        Assert.Equal(2, capturedFileContexts.Count);
        Assert.All(capturedPullRequests.Take(2), filePr => Assert.Single(filePr.ChangedFiles));
        var perFileSessions = capturedPullRequests
            .Take(2)
            .Select((filePr, index) => new
            {
                FilePath = filePr.ChangedFiles[0].Path,
                Session = Assert.IsType<AgentReviewSession>(capturedFileContexts[index].ReviewSession),
            })
            .ToDictionary(entry => entry.FilePath, entry => entry.Session, StringComparer.Ordinal);

        Assert.Equal(2, perFileSessions.Count);
        Assert.Contains("src/Foo.cs", perFileSessions.Keys);
        Assert.Contains("src/Bar.cs", perFileSessions.Keys);

        var firstSession = perFileSessions["src/Foo.cs"];
        var secondSession = perFileSessions["src/Bar.cs"];
        Assert.NotEqual(firstSession.LocalSessionId, secondSession.LocalSessionId);
        Assert.Equal(firstSession.LocalSessionId, firstSession.ConversationOwnerId);
        Assert.Equal(secondSession.LocalSessionId, secondSession.ConversationOwnerId);
        Assert.True(firstSession.RemoteConversationId is "conv-a" or "conv-b");
        Assert.True(secondSession.RemoteConversationId is "conv-a" or "conv-b");
        Assert.NotEqual(firstSession.RemoteConversationId, secondSession.RemoteConversationId);
        Assert.NotEqual("stale-base-session", firstSession.LocalSessionId);
        Assert.NotEqual("stale-base-session", secondSession.LocalSessionId);
        Assert.NotEqual("stale-base-conversation", firstSession.RemoteConversationId);
        Assert.NotEqual("stale-base-conversation", secondSession.RemoteConversationId);
    }

    [Fact]
    public async Task ReviewAsync_WithPromptExperiment_PassesExperimentToPerFileReviewAndSynthesis()
    {
        ReviewSystemContext? capturedFileContext = null;
        List<(ChatRole Role, string? Text)>? capturedSynthesisMessages = null;

        var promptExperiment = new PromptExperimentContext(
            "variant-a",
            [
                new StagePromptVariant(
                    PromptStageKeys.SynthesisSystem,
                    PromptStageRole.System,
                    PromptCompositionMode.Prepend,
                    "Variant synthesis header"),
                new StagePromptVariant(
                    PromptStageKeys.SynthesisUser,
                    PromptStageRole.User,
                    PromptCompositionMode.Append,
                    "Variant synthesis tail"),
            ]);

        var aiCore = Substitute.For<IAiReviewCore>();
        aiCore.ReviewAsync(
                Arg.Any<PullRequest>(),
                Arg.Do<ReviewSystemContext>(context => capturedFileContext = context),
                Arg.Any<CancellationToken>())
            .Returns(
                new ReviewResult(
                    "file summary",
                    [new ReviewComment("src/Foo.cs", 10, CommentSeverity.Warning, "Potential issue")]));

        var job = CreateJob();
        var pr = CreatePr(CreateFile("src/Foo.cs"));
        var storedResults = new List<ReviewFileResult>();
        var repo = Substitute.For<IJobRepository>();
        repo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<ReviewJob?>(BuildJobWithResults(job, storedResults)));
        repo.AddFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                storedResults.Add(callInfo.Arg<ReviewFileResult>());
                return Task.CompletedTask;
            });
        repo.UpdateFileResultAsync(Arg.Any<ReviewFileResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Do<IList<ChatMessage>>(messages =>
                    capturedSynthesisMessages = messages.Select(message => (message.Role, (string?)message.Text)).ToList()),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"summary":"Synthesized summary.","cross_cutting_concerns":[]}""")));

        var sut = CreateOrchestrator(aiCore, CreateProtocolRecorder(), repo, chatClient);
        var context = new ReviewSystemContext(null, [], null)
        {
            PromptExperiment = promptExperiment,
        };

        var result = await sut.ReviewAsync(job, pr, context, CancellationToken.None);

        Assert.Equal("Synthesized summary.", result.Summary);
        Assert.NotNull(capturedFileContext);
        Assert.Same(promptExperiment, capturedFileContext!.PromptExperiment);
        Assert.NotNull(capturedSynthesisMessages);
        Assert.Contains(
            capturedSynthesisMessages!,
            message => message.Role == ChatRole.System
                       && message.Text is not null
                       && message.Text.StartsWith("Variant synthesis header", StringComparison.Ordinal));
        Assert.Contains(
            capturedSynthesisMessages!,
            message => message.Role == ChatRole.User
                       && message.Text is not null
                       && message.Text.Contains("Variant synthesis tail", StringComparison.Ordinal));
    }
}
