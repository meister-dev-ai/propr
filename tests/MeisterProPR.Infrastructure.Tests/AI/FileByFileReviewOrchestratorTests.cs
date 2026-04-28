// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.AI;
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
        IAiChatClientFactory? aiClientFactory = null)
    {
        return new FileByFileReviewOrchestrator(
            aiCore,
            protocolRecorder,
            jobRepository,
            chatClient,
            options ?? DefaultOptions(),
            Substitute.For<ILogger<FileByFileReviewOrchestrator>>(),
            aiConnectionRepository,
            aiClientFactory);
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

        var sut = CreateOrchestrator(aiCore, CreateProtocolRecorder(), repo, Substitute.For<IChatClient>());

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

    // T033 — A failed file does not stop remaining files
    [Fact]
    public async Task ReviewAsync_OneFileFails_OtherFilesStillReviewed()
    {
        var aiCore = Substitute.For<IAiReviewCore>();
        var callCount = 0;

        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
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

        var jobWithResults = CreateJob();
        // Must return the same job id
        var repo = Substitute.For<IJobRepository>();
        // Return job with one completed file result
        repo.GetByIdWithFileResultsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(null));
        // We need a ReviewJob whose FileReviewResults contains completedResult
        // Use a sub that supports FileReviewResults collection inspection  
        repo.GetByIdWithFileResultsAsync(job.Id, Arg.Any<CancellationToken>())
            .Returns(ci =>
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
            modelId: "gpt-4o-high",
            purpose: AiPurpose.ReviewHighEffort,
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
}
