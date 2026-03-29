using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.Services;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterProPR.Application.Tests.Services;

public class ReviewOrchestrationServiceTests
{
    private static (
        IJobRepository jobs,
        IPullRequestFetcher prFetcher,
        IFileByFileReviewOrchestrator orchestrator,
        IAdoCommentPoster commentPoster,
        IAdoReviewerManager reviewerManager,
        IClientRegistry clientRegistry,
        IReviewPrScanRepository prScanRepository,
        IRepositoryInstructionFetcher instructionFetcher,
        IRepositoryInstructionEvaluator instructionEvaluator,
        ILogger<ReviewOrchestrationService> logger) CreateDeps()
    {
        var prScanRepository = Substitute.For<IReviewPrScanRepository>();
        prScanRepository.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(null));
        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetCommentResolutionBehaviorAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CommentResolutionBehavior.Silent));
        clientRegistry.GetCustomSystemMessageAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        var instructionFetcher = Substitute.For<IRepositoryInstructionFetcher>();
        instructionFetcher
            .FetchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RepositoryInstruction>>([]));
        var instructionEvaluator = Substitute.For<IRepositoryInstructionEvaluator>();
        instructionEvaluator
            .EvaluateRelevanceAsync(Arg.Any<IReadOnlyList<RepositoryInstruction>>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RepositoryInstruction>>([]));
        return (
            Substitute.For<IJobRepository>(),
            Substitute.For<IPullRequestFetcher>(),
            Substitute.For<IFileByFileReviewOrchestrator>(),
            Substitute.For<IAdoCommentPoster>(),
            Substitute.For<IAdoReviewerManager>(),
            clientRegistry,
            prScanRepository,
            instructionFetcher,
            instructionEvaluator,
            Substitute.For<ILogger<ReviewOrchestrationService>>());
    }

    private static ReviewJob CreateJob()
    {
        return new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
    }

    private static PullRequest CreatePullRequest(IReadOnlyList<PrCommentThread>? threads = null)
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
            new List<ChangedFile>().AsReadOnly(),
            PrStatus.Active,
            threads);
    }

    private static ReviewResult CreateReviewResult()
    {
        return new ReviewResult("Summary", new List<ReviewComment>().AsReadOnly());
    }

    private static ReviewOrchestrationService CreateService(
        IJobRepository jobs,
        IPullRequestFetcher prFetcher,
        IFileByFileReviewOrchestrator orchestrator,
        IAdoCommentPoster commentPoster,
        IAdoReviewerManager reviewerManager,
        IClientRegistry clientRegistry,
        IReviewPrScanRepository prScanRepository,
        ILogger<ReviewOrchestrationService> logger,
        IRepositoryInstructionFetcher? instructionFetcher = null,
        IRepositoryInstructionEvaluator? instructionEvaluator = null,
        IRepositoryExclusionFetcher? exclusionFetcher = null)
    {
        var threadClient = Substitute.For<IAdoThreadClient>();
        var threadReplier = Substitute.For<IAdoThreadReplier>();
        var resolutionCore = Substitute.For<IAiCommentResolutionCore>();
        var reviewContextToolsFactory = Substitute.For<IReviewContextToolsFactory>();
        reviewContextToolsFactory
            .Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<Guid?>())
            .Returns(Substitute.For<IReviewContextTools>());

        var fetcher = instructionFetcher ?? CreateDefaultInstructionFetcher();
        var evaluator = instructionEvaluator ?? CreateDefaultInstructionEvaluator();
        var exclusionFetcherResolved = exclusionFetcher ?? CreateDefaultExclusionFetcher();

        return new ReviewOrchestrationService(
            jobs,
            prFetcher,
            orchestrator,
            commentPoster,
            reviewerManager,
            clientRegistry,
            prScanRepository,
            threadClient,
            threadReplier,
            resolutionCore,
            Substitute.For<IProtocolRecorder>(),
            reviewContextToolsFactory,
            fetcher,
            exclusionFetcherResolved,
            evaluator,
            Substitute.For<IOptions<AiReviewOptions>>(),
            logger);
    }

    private static IRepositoryInstructionFetcher CreateDefaultInstructionFetcher()
    {
        var fetcher = Substitute.For<IRepositoryInstructionFetcher>();
        fetcher
            .FetchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RepositoryInstruction>>([]));
        return fetcher;
    }

    private static IRepositoryInstructionEvaluator CreateDefaultInstructionEvaluator()
    {
        var evaluator = Substitute.For<IRepositoryInstructionEvaluator>();
        evaluator
            .EvaluateRelevanceAsync(Arg.Any<IReadOnlyList<RepositoryInstruction>>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RepositoryInstruction>>([]));
        return evaluator;
    }

    private static IRepositoryExclusionFetcher CreateDefaultExclusionFetcher()
    {
        var fetcher = Substitute.For<IRepositoryExclusionFetcher>();
        fetcher
            .FetchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ReviewExclusionRules.Empty));
        return fetcher;
    }

    /// <summary>Set up the clientRegistry to return a non-null reviewerId for the given job's ClientId.</summary>
    private static void SetupReviewerIdReturns(IClientRegistry clientRegistry, ReviewJob job, Guid reviewerId)
    {
        clientRegistry.GetReviewerIdAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Guid?>(reviewerId));
    }

    [Fact]
    public async Task ProcessAsync_AiException_TransitionsJobToFailed()
    {
        // Arrange
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, instructionFetcher, instructionEvaluator, logger) =
            CreateDeps();

        var job = CreateJob();
        var pr = CreatePullRequest();

        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Throws(new Exception("AI error"));

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        // Act
        await service.ProcessAsync(job, CancellationToken.None);

        // Assert
        await jobs.Received(1).SetFailedAsync(job.Id, Arg.Is<string>(s => s.Contains("AI error")));
        await jobs.DidNotReceive().SetResultAsync(Arg.Any<Guid>(), Arg.Any<ReviewResult>());
    }

    // T025 — AddOptionalReviewerAsync is called with client's ReviewerId before PostAsync

    // T007 (US1) — reviewContextToolsFactory.Create is called with pr.SourceBranch as the 4th argument
    [Fact]
    public async Task ProcessAsync_PassesPrSourceBranchToReviewContextToolsFactory()
    {
        // Arrange
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, instructionFetcher, instructionEvaluator, logger) =
            CreateDeps();

        var job = CreateJob();
        const string expectedSourceBranch = "refs/heads/feature/my-pr";
        var pr = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            1,
            "Test PR",
            null,
            expectedSourceBranch,
            "main",
            new List<ChangedFile>().AsReadOnly(),
            PrStatus.Active,
            null);

        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("Summary", new List<ReviewComment>().AsReadOnly()));

        var reviewContextToolsFactory = Substitute.For<IReviewContextToolsFactory>();
        reviewContextToolsFactory
            .Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<Guid?>())
            .Returns(Substitute.For<IReviewContextTools>());

        var threadClient = Substitute.For<IAdoThreadClient>();
        var threadReplier = Substitute.For<IAdoThreadReplier>();
        var resolutionCore = Substitute.For<IAiCommentResolutionCore>();

        var service = new ReviewOrchestrationService(
            jobs,
            prFetcher,
            orchestrator,
            commentPoster,
            reviewerManager,
            clientRegistry,
            prScanRepository,
            threadClient,
            threadReplier,
            resolutionCore,
            Substitute.For<IProtocolRecorder>(),
            reviewContextToolsFactory,
            instructionFetcher,
            CreateDefaultExclusionFetcher(),
            instructionEvaluator,
            Substitute.For<Microsoft.Extensions.Options.IOptions<AiReviewOptions>>(),
            logger);

        // Act
        await service.ProcessAsync(job, CancellationToken.None);

        // Assert — 4th arg (sourceBranch) must be the PR's source branch
        reviewContextToolsFactory.Received(1).Create(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            expectedSourceBranch,
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<Guid?>());
    }

    [Fact]
    public async Task ProcessAsync_CallsAddOptionalReviewerBeforePostAsync()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, instructionFetcher, instructionEvaluator, logger) =
            CreateDeps();

        var job = CreateJob();
        var pr = CreatePullRequest();
        var result = CreateReviewResult();
        var reviewerId = Guid.NewGuid();

        SetupReviewerIdReturns(clientRegistry, job, reviewerId);
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(result);

        var callOrder = new List<string>();
        reviewerManager.AddOptionalReviewerAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<Guid>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("reviewer"));

        commentPoster.PostAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<ReviewResult>(),
                Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyList<PrCommentThread>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("post"));

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);
        await service.ProcessAsync(job, CancellationToken.None);

        Assert.Equal(["reviewer", "post"], callOrder);
    }

    [Fact]
    public async Task ProcessAsync_CommentPostException_TransitionsJobToFailed()
    {
        // Arrange
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, instructionFetcher, instructionEvaluator, logger) =
            CreateDeps();

        var job = CreateJob();
        var pr = CreatePullRequest();
        var result = CreateReviewResult();

        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(result);
        commentPoster.PostAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<ReviewResult>(),
                Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyList<PrCommentThread>?>(),
                Arg.Any<CancellationToken>())
            .Throws(new Exception("Comment post error"));

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        // Act
        await service.ProcessAsync(job, CancellationToken.None);

        // Assert
        await jobs.Received(1).SetFailedAsync(job.Id, Arg.Is<string>(s => s.Contains("Comment post error")));
    }

    [Fact]
    public async Task ProcessAsync_FetchException_TransitionsJobToFailed()
    {
        // Arrange
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, instructionFetcher, instructionEvaluator, logger) =
            CreateDeps();

        var job = CreateJob();
        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Throws(new Exception("ADO fetch error"));

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        // Act
        await service.ProcessAsync(job, CancellationToken.None);

        // Assert
        await jobs.Received(1).SetFailedAsync(job.Id, Arg.Is<string>(s => s.Contains("ADO fetch error")));
        await jobs.DidNotReceive().SetResultAsync(Arg.Any<Guid>(), Arg.Any<ReviewResult>());
    }

    // T032 — null ReviewerId → SetFailed "not configured", no reviewer call, no PostAsync

    [Fact]
    public async Task ProcessAsync_NullReviewerId_CallsSetFailedWithNotConfiguredMessage()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, instructionFetcher, instructionEvaluator, logger) =
            CreateDeps();

        var clientId = Guid.NewGuid();
        var job = new ReviewJob(Guid.NewGuid(), clientId, "https://dev.azure.com/org", "proj", "repo", 1, 1);

        clientRegistry.GetReviewerIdAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Guid?>(null));

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        await service.ProcessAsync(job, CancellationToken.None);

        await jobs.Received(1).SetFailedAsync(job.Id, Arg.Is<string>(s => s.Contains("not configured")));
        await reviewerManager.DidNotReceive()
            .AddOptionalReviewerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await commentPoster.DidNotReceive()
            .PostAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ReviewResult>(), Arg.Any<Guid?>(), Arg.Any<IReadOnlyList<PrCommentThread>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_PassesExistingThreadsToCommentPoster()
    {
        // Arrange
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, instructionFetcher, instructionEvaluator, logger) =
            CreateDeps();

        var job = CreateJob();
        var threads = new List<PrCommentThread>
        {
            new(
                1,
                "/src/Foo.cs",
                5,
                new List<PrThreadComment>
                {
                    new("Bot", "ERROR: Null ref."),
                }.AsReadOnly()),
        }.AsReadOnly();
        var pr = CreatePullRequest(threads);
        var result = CreateReviewResult();

        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(result);

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        // Act
        await service.ProcessAsync(job, CancellationToken.None);

        // Assert - existing threads are forwarded to the comment poster
        await commentPoster.Received(1)
            .PostAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<ReviewResult>(),
                Arg.Any<Guid?>(),
                threads,
                Arg.Any<CancellationToken>());
    }


    [Fact]
    public async Task ProcessAsync_CompletedPrAtFetchTime_CallsSetFailedWithoutCallingAi()
    {
        // T019 renames: Completed PR still calls SetFailedAsync (only Abandoned triggers SetCancelledAsync)
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, instructionFetcher, instructionEvaluator, logger) =
            CreateDeps();

        var job = CreateJob();
        var closedPr = CreatePullRequest() with { Status = PrStatus.Completed };

        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(closedPr);

        var sut = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        // Act
        await sut.ProcessAsync(job, CancellationToken.None);

        // Assert: job is marked failed with the EC-002 message; AI is never called
        await jobs.Received(1).SetFailedAsync(job.Id, Arg.Is<string>(m => m.Contains("closed or abandoned")));
        await orchestrator.DidNotReceive().ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
    }

    // T034 — AddOptionalReviewerAsync throws → PostAsync NOT called, job fails

    [Fact]
    public async Task ProcessAsync_ReviewerAddThrows_PostAsyncNotCalledAndJobFails()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, instructionFetcher, instructionEvaluator, logger) =
            CreateDeps();

        var job = CreateJob();
        var pr = CreatePullRequest();

        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);
        reviewerManager.AddOptionalReviewerAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<Guid>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Throws(new Exception("Permission denied"));

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        await service.ProcessAsync(job, CancellationToken.None);

        await jobs.Received(1).SetFailedAsync(job.Id, Arg.Any<string>());
        await commentPoster.DidNotReceive()
            .PostAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ReviewResult>(), Arg.Any<Guid?>(), Arg.Any<IReadOnlyList<PrCommentThread>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_SuccessfulFlow_CallsCommentPosterWithCorrectParameters()
    {
        // Arrange
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, instructionFetcher, instructionEvaluator, logger) =
            CreateDeps();

        var job = CreateJob();
        var pr = CreatePullRequest();
        var result = CreateReviewResult();

        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(result);

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        // Act
        await service.ProcessAsync(job, CancellationToken.None);

        // Assert
        await commentPoster.Received(1)
            .PostAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.IterationId,
                result,
                Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyList<PrCommentThread>?>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_SuccessfulFlow_TransitionsJobToCompleted()
    {
        // Arrange
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, instructionFetcher, instructionEvaluator, logger) =
            CreateDeps();

        var job = CreateJob();
        var pr = CreatePullRequest();
        var result = CreateReviewResult();

        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(result);

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        // Act
        await service.ProcessAsync(job, CancellationToken.None);

        // Assert
        await jobs.Received(1).SetResultAsync(job.Id, result);
        await commentPoster.Received(1)
            .PostAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                result,
                Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyList<PrCommentThread>?>(),
                Arg.Any<CancellationToken>());
        await jobs.DidNotReceive().SetFailedAsync(Arg.Any<Guid>(), Arg.Any<string>());
    }

    // T027 / T005 — Skip logic: same iteration ID + no new thread replies → AI not called, job deleted (no DB row persisted)

    [Fact]
    public async Task ProcessAsync_SameIterationNoNewReplies_SkipsAiReviewAndDeletesJob()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, instructionFetcher, instructionEvaluator, logger) =
            CreateDeps();

        var reviewerId = Guid.NewGuid();
        var job = CreateJob(); // IterationId = 1
        var pr = CreatePullRequest();

        SetupReviewerIdReturns(clientRegistry, job, reviewerId);
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);

        // Scan exists with same iteration ID as job.IterationId (1 → "1")
        var existingScan = new ReviewPrScan(Guid.NewGuid(), job.ClientId, job.RepositoryId, job.PullRequestId, "1");
        prScanRepository.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(existingScan));

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        await service.ProcessAsync(job, CancellationToken.None);

        // AI review must NOT be called (no new commits or replies)
        await orchestrator.DidNotReceiveWithAnyArgs().ReviewAsync(null!, null!, null!, Arg.Any<CancellationToken>());
        // Job must be deleted — no DB row should remain for an idle cycle
        await jobs.Received(1).DeleteAsync(job.Id, Arg.Any<CancellationToken>());
        await jobs.DidNotReceive().SetResultAsync(Arg.Any<Guid>(), Arg.Any<ReviewResult>());
        await jobs.DidNotReceive().SetFailedAsync(Arg.Any<Guid>(), Arg.Any<string>());
    }

    // T005 — Empty AI review response -> no comment posted, job deleted (not persisted)

    [Fact]
    public async Task ProcessAsync_EmptyAiReviewResult_DeletesJobWithoutPosting()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, instructionFetcher, instructionEvaluator, logger) =
            CreateDeps();

        var job = CreateJob();
        var pr = CreatePullRequest();
        var emptyResult = new ReviewResult("   ", new List<ReviewComment>().AsReadOnly());

        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(emptyResult);

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        await service.ProcessAsync(job, CancellationToken.None);

        // No comment should be posted for an empty review
        await commentPoster.DidNotReceiveWithAnyArgs()
            .PostAsync(null!, null!, null!, 0, 0, null!);
        // Job must be deleted — empty review is treated as no-op
        await jobs.Received(1).DeleteAsync(job.Id, Arg.Any<CancellationToken>());
        await jobs.DidNotReceive().SetResultAsync(Arg.Any<Guid>(), Arg.Any<ReviewResult>());
        await jobs.DidNotReceive().SetFailedAsync(Arg.Any<Guid>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ProcessAsync_SameIterationButNewRepliesOnReviewerThread_RunsConversationalPath()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, instructionFetcher, instructionEvaluator, logger) =
            CreateDeps();

        var reviewerId = Guid.NewGuid();
        var job = CreateJob(); // IterationId = 1
        var result = CreateReviewResult();
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>()).Returns(result);

        // Thread authored by reviewerId with 2 comments currently
        var thread = new PrCommentThread(
            42,
            "/src/Foo.cs",
            10,
            new List<PrThreadComment>
            {
                new("Bot", "Please fix this.", reviewerId),
                new("Dev", "I think it's fine."),
            }.AsReadOnly());

        var pr = CreatePullRequest(new List<PrCommentThread> { thread }.AsReadOnly());

        SetupReviewerIdReturns(clientRegistry, job, reviewerId);
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);

        // Scan has same iteration but stored 0 non-reviewer replies for this thread.
        // Thread now has 1 non-reviewer reply ("Dev") so a new user reply is detected.
        var existingScan = new ReviewPrScan(Guid.NewGuid(), job.ClientId, job.RepositoryId, job.PullRequestId, "1");
        existingScan.Threads.Add(
            new ReviewPrScanThread
            {
                ReviewPrScanId = existingScan.Id,
                ThreadId = 42,
                LastSeenReplyCount = 0, // only non-reviewer comments are counted; 1 now > 0 stored → new reply
            });
        prScanRepository.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(existingScan));

        var resolutionCore = Substitute.For<IAiCommentResolutionCore>();
        resolutionCore.EvaluateConversationalReplyAsync(Arg.Any<PrCommentThread>(), Arg.Any<CancellationToken>())
            .Returns(new ThreadResolutionResult(false, null));

        // Build service with custom resolutionCore
        var stubToolsFactory = Substitute.For<IReviewContextToolsFactory>();
        stubToolsFactory
            .Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<Guid?>())
            .Returns(Substitute.For<IReviewContextTools>());
        var service = new ReviewOrchestrationService(
            jobs,
            prFetcher,
            orchestrator,
            commentPoster,
            reviewerManager,
            clientRegistry,
            prScanRepository,
            Substitute.For<IAdoThreadClient>(),
            Substitute.For<IAdoThreadReplier>(),
            resolutionCore,
            Substitute.For<IProtocolRecorder>(),
            stubToolsFactory,
            CreateDefaultInstructionFetcher(),
            CreateDefaultExclusionFetcher(),
            CreateDefaultInstructionEvaluator(),
            Substitute.For<IOptions<AiReviewOptions>>(),
            logger);

        await service.ProcessAsync(job, CancellationToken.None);

        // Conversational path was invoked (not code-change)
        await resolutionCore.Received(1)
            .EvaluateConversationalReplyAsync(
                Arg.Is<PrCommentThread>(t => t.ThreadId == 42),
                Arg.Any<CancellationToken>());
        await resolutionCore.DidNotReceiveWithAnyArgs()
            .EvaluateCodeChangeAsync(null!, null!, Arg.Any<CancellationToken>());

        // File-by-file review must NOT run — no new commit was pushed
        await orchestrator.DidNotReceiveWithAnyArgs()
            .ReviewAsync(null!, null!, null!, Arg.Any<CancellationToken>(), null);

        // Job must be cleaned up after conversational reply
        await jobs.Received(1).DeleteAsync(job.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_NewIteration_OnlyEvaluatesThreadsAuthoredByReviewer()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, instructionFetcher, instructionEvaluator, logger) =
            CreateDeps();

        var reviewerId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var job = CreateJob(); // IterationId = 1
        var result = CreateReviewResult();
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>()).Returns(result);

        // Two threads: one by reviewer, one by someone else
        var reviewerThread = new PrCommentThread(
            10,
            "/src/A.cs",
            1,
            new List<PrThreadComment>
            {
                new("Bot", "Reviewer comment", reviewerId),
            }.AsReadOnly());

        var otherThread = new PrCommentThread(
            20,
            "/src/B.cs",
            2,
            new List<PrThreadComment>
            {
                new("Human", "Human comment", otherId),
            }.AsReadOnly());

        var pr = CreatePullRequest(new List<PrCommentThread> { reviewerThread, otherThread }.AsReadOnly());

        SetupReviewerIdReturns(clientRegistry, job, reviewerId);
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);

        // No existing scan → new iteration path
        prScanRepository.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(null));

        var resolutionCore = Substitute.For<IAiCommentResolutionCore>();
        resolutionCore.EvaluateCodeChangeAsync(Arg.Any<PrCommentThread>(), Arg.Any<PullRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ThreadResolutionResult(false, null));

        var stubToolsFactory2 = Substitute.For<IReviewContextToolsFactory>();
        stubToolsFactory2
            .Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<Guid?>())
            .Returns(Substitute.For<IReviewContextTools>());
        var service = new ReviewOrchestrationService(
            jobs,
            prFetcher,
            orchestrator,
            commentPoster,
            reviewerManager,
            clientRegistry,
            prScanRepository,
            Substitute.For<IAdoThreadClient>(),
            Substitute.For<IAdoThreadReplier>(),
            resolutionCore,
            Substitute.For<IProtocolRecorder>(),
            stubToolsFactory2,
            CreateDefaultInstructionFetcher(),
            CreateDefaultExclusionFetcher(),
            CreateDefaultInstructionEvaluator(),
            Substitute.For<IOptions<AiReviewOptions>>(),
            logger);

        await service.ProcessAsync(job, CancellationToken.None);

        // Only the reviewer-authored thread should be evaluated
        await resolutionCore.Received(1)
            .EvaluateCodeChangeAsync(
                Arg.Is<PrCommentThread>(t => t.ThreadId == 10),
                Arg.Any<PullRequest>(),
                Arg.Any<CancellationToken>());

        // The other author's thread must NOT be evaluated
        await resolutionCore.DidNotReceive()
            .EvaluateCodeChangeAsync(
                Arg.Is<PrCommentThread>(t => t.ThreadId == 20),
                Arg.Any<PullRequest>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_SuccessfulFlow_SavesScanWithCurrentIteration()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, instructionFetcher, instructionEvaluator, logger) =
            CreateDeps();

        var job = CreateJob(); // IterationId = 1
        var pr = CreatePullRequest();
        var result = CreateReviewResult();

        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(result);

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        await service.ProcessAsync(job, CancellationToken.None);

        // Scan must be upserted with the current iteration ID
        await prScanRepository.Received(1)
            .UpsertAsync(
                Arg.Is<ReviewPrScan>(s =>
                    s.LastProcessedCommitId == job.IterationId.ToString() &&
                    s.PullRequestId == job.PullRequestId &&
                    s.RepositoryId == job.RepositoryId),
                Arg.Any<CancellationToken>());
    }

    // T019 — ReviewOrchestrationService passes ReviewSystemContext with non-null ReviewTools

    [Fact]
    public async Task ProcessAsync_PassesReviewContextToolsToAiCore()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, instructionFetcher, instructionEvaluator, logger) =
            CreateDeps();

        var job = CreateJob();
        var pr = CreatePullRequest();
        var result = CreateReviewResult();

        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(result);

        ReviewSystemContext? capturedContext = null;
        orchestrator
            .When(x => x.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>()))
            .Do(ci => capturedContext = ci.ArgAt<ReviewSystemContext>(2));

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        await service.ProcessAsync(job, CancellationToken.None);

        // ReviewTools must be non-null (factory always creates tools)
        Assert.NotNull(capturedContext);
        Assert.NotNull(capturedContext!.ReviewTools);
    }

    // T019 — Target branch (not source branch) is what gets passed for repository instruction fetch

    [Fact]
    public async Task ProcessAsync_PassesTargetBranchInSystemContext()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, instructionFetcher, instructionEvaluator, logger) =
            CreateDeps();

        var job = CreateJob();
        var pr = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            1,
            "Test PR",
            null,
            "feature/my-branch",
            "main",
            new List<ChangedFile>().AsReadOnly());
        var result = CreateReviewResult();

        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(result);

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger, instructionFetcher);

        await service.ProcessAsync(job, CancellationToken.None);

        // Target branch ("main") must be passed to instructionFetcher, not the source branch
        await instructionFetcher.Received(1)
            .FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                "main",
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>());

        await instructionFetcher.DidNotReceive()
            .FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                "feature/my-branch",
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>());
    }

    // T026 — GetReviewerIdAsync returns non-null → AddOptionalReviewerAsync called with that GUID

    [Fact]
    public async Task ProcessAsync_WithConfiguredReviewerId_CallsAddOptionalReviewerWithThatGuid()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, instructionFetcher, instructionEvaluator, logger) =
            CreateDeps();

        var job = CreateJob();
        var pr = CreatePullRequest();
        var result = CreateReviewResult();
        var reviewerId = Guid.NewGuid();

        SetupReviewerIdReturns(clientRegistry, job, reviewerId);
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(result);

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);
        await service.ProcessAsync(job, CancellationToken.None);

        await reviewerManager.Received(1)
            .AddOptionalReviewerAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                reviewerId,
                job.ClientId,
                Arg.Any<CancellationToken>());
    }

    // T029 — Prompt injection prevention: IRepositoryInstructionFetcher is always called with TargetBranch, not SourceBranch

    [Fact]
    public async Task ProcessAsync_InstructionFetcherCalledWithTargetBranch_NotSourceBranch()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, instructionFetcher, instructionEvaluator, logger) =
            CreateDeps();

        var job = CreateJob();
        // PR with distinct source and target branches
        var pr = new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            "repo",
            1,
            1,
            "Test PR",
            null,
            "feature/potentially-malicious-branch",
            "main",
            new List<ChangedFile>().AsReadOnly());
        var result = CreateReviewResult();

        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(result);

        var service = CreateService(
            jobs,
            prFetcher,
            orchestrator,
            commentPoster,
            reviewerManager,
            clientRegistry,
            prScanRepository,
            logger,
            instructionFetcher,
            instructionEvaluator);

        await service.ProcessAsync(job, CancellationToken.None);

        // Assert: fetcher was called with the TARGET branch ("main"), NOT the source branch
        await instructionFetcher.Received(1)
            .FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                "main", // target branch — not "feature/potentially-malicious-branch"
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>());

        // Confirm source branch was never passed to the fetcher
        await instructionFetcher.DidNotReceive()
            .FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                "feature/potentially-malicious-branch",
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>());
    }

    // T038 — ReviewOrchestrationService populates ReviewSystemContext.ClientSystemMessage

    [Fact]
    public async Task ProcessAsync_ClientSystemMessage_ReachesAiReviewCore()
    {
        // Arrange
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _, logger) =
            CreateDeps();

        var job = CreateJob();
        var pr = CreatePullRequest();
        var result = CreateReviewResult();
        var expectedMessage = "Always flag security changes for senior review.";

        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        clientRegistry.GetCustomSystemMessageAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(expectedMessage));
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(result);

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        // Act
        await service.ProcessAsync(job, CancellationToken.None);

        // Assert — IAiReviewCore received a ReviewSystemContext with the expected ClientSystemMessage
        await orchestrator.Received(1)
            .ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Is<ReviewSystemContext>(ctx => ctx.ClientSystemMessage == expectedMessage),
                Arg.Any<CancellationToken>());
    }

    // O1/O6 — threads with a resolved ADO status are skipped; resolution AI is never called for them

    [Fact]
    public async Task ProcessAsync_ThreadWithFixedStatus_IsSkippedWithoutAiCall()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _, logger) =
            CreateDeps();

        var reviewerId = Guid.NewGuid();
        var job = CreateJob();
        var result = CreateReviewResult();
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>()).Returns(result);

        // Reviewer-authored thread that ADO already set to "Fixed"
        var fixedThread = new PrCommentThread(
            99,
            "/src/Foo.cs",
            5,
            new List<PrThreadComment> { new("Bot", "Fix this", reviewerId) }.AsReadOnly(),
            "Fixed");

        var pr = CreatePullRequest(new List<PrCommentThread> { fixedThread }.AsReadOnly());

        SetupReviewerIdReturns(clientRegistry, job, reviewerId);
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);

        var resolutionCore = Substitute.For<IAiCommentResolutionCore>();
        var stubToolsFactory = Substitute.For<IReviewContextToolsFactory>();
        stubToolsFactory
            .Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<Guid?>())
            .Returns(Substitute.For<IReviewContextTools>());

        var service = new ReviewOrchestrationService(
            jobs,
            prFetcher,
            orchestrator,
            commentPoster,
            reviewerManager,
            clientRegistry,
            prScanRepository,
            Substitute.For<IAdoThreadClient>(),
            Substitute.For<IAdoThreadReplier>(),
            resolutionCore,
            Substitute.For<IProtocolRecorder>(),
            stubToolsFactory,
            CreateDefaultInstructionFetcher(),
            CreateDefaultExclusionFetcher(),
            CreateDefaultInstructionEvaluator(),
            Substitute.For<IOptions<AiReviewOptions>>(),
            logger);

        await service.ProcessAsync(job, CancellationToken.None);

        // Resolution AI must not be called at all — the thread is already fixed
        await resolutionCore.DidNotReceiveWithAnyArgs()
            .EvaluateCodeChangeAsync(null!, null!);
        await resolutionCore.DidNotReceiveWithAnyArgs()
            .EvaluateConversationalReplyAsync(null!);
    }

    [Theory]
    [InlineData("Fixed")]
    [InlineData("Closed")]
    [InlineData("WontFix")]
    [InlineData("ByDesign")]
    public async Task ProcessAsync_ThreadWithResolvedStatus_NeverCallsResolutionAi(string resolvedStatus)
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _, logger) =
            CreateDeps();

        var reviewerId = Guid.NewGuid();
        var job = CreateJob();
        var result = CreateReviewResult();
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>()).Returns(result);

        var resolvedThread = new PrCommentThread(
            100,
            "/src/Bar.cs",
            10,
            new List<PrThreadComment> { new("Bot", "Concern here", reviewerId) }.AsReadOnly(),
            resolvedStatus);

        var pr = CreatePullRequest(new List<PrCommentThread> { resolvedThread }.AsReadOnly());

        SetupReviewerIdReturns(clientRegistry, job, reviewerId);
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);

        var resolutionCore = Substitute.For<IAiCommentResolutionCore>();
        var stubToolsFactory = Substitute.For<IReviewContextToolsFactory>();
        stubToolsFactory
            .Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<Guid?>())
            .Returns(Substitute.For<IReviewContextTools>());

        var service = new ReviewOrchestrationService(
            jobs,
            prFetcher,
            orchestrator,
            commentPoster,
            reviewerManager,
            clientRegistry,
            prScanRepository,
            Substitute.For<IAdoThreadClient>(),
            Substitute.For<IAdoThreadReplier>(),
            resolutionCore,
            Substitute.For<IProtocolRecorder>(),
            stubToolsFactory,
            CreateDefaultInstructionFetcher(),
            CreateDefaultExclusionFetcher(),
            CreateDefaultInstructionEvaluator(),
            Substitute.For<IOptions<AiReviewOptions>>(),
            logger);

        await service.ProcessAsync(job, CancellationToken.None);

        await resolutionCore.DidNotReceiveWithAnyArgs()
            .EvaluateCodeChangeAsync(null!, null!);
        await resolutionCore.DidNotReceiveWithAnyArgs()
            .EvaluateConversationalReplyAsync(null!);
    }

    // --- T012: US1 PR Abandonment tests (failing until T019 is implemented) ---

    [Fact]
    public async Task ProcessAsync_AbandonedPr_CallsSetCancelledAsyncNotSetFailedAsync()
    {
        // T012 (a): pr.Status == Abandoned → SetCancelledAsync called, no AI calls
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, instructionFetcher, instructionEvaluator, logger) =
            CreateDeps();

        var job = CreateJob();
        var abandonedPr = CreatePullRequest() with { Status = PrStatus.Abandoned };

        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(abandonedPr);

        var sut = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        // Act
        await sut.ProcessAsync(job, CancellationToken.None);

        // Assert: SetCancelledAsync called; SetFailedAsync NOT called; AI not invoked
        await jobs.Received(1).SetCancelledAsync(job.Id, Arg.Any<CancellationToken>());
        await jobs.DidNotReceive().SetFailedAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await orchestrator.DidNotReceive().ReviewAsync(
            Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_JobCancelledBetweenPrFetchAndFileReview_ExitsWithoutAiCalls()
    {
        // T012 (b): job status flips to Cancelled after PR fetch → no AI calls
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, instructionFetcher, instructionEvaluator, logger) =
            CreateDeps();

        var job = CreateJob();
        var pr = CreatePullRequest();

        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(pr);

        // GetById returns a Cancelled job (CAS by another worker)
        var cancelledJob = new ReviewJob(job.Id, job.ClientId, job.OrganizationUrl, job.ProjectId, job.RepositoryId, job.PullRequestId, job.IterationId);
        cancelledJob.Status = JobStatus.Cancelled;
        jobs.GetById(job.Id).Returns(cancelledJob);

        var sut = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        // Act
        await sut.ProcessAsync(job, CancellationToken.None);

        // Assert: orchestrator never called; no comment posted
        await orchestrator.DidNotReceive().ReviewAsync(
            Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
        await commentPoster.DidNotReceive().PostAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ReviewResult>(),
            Arg.Any<Guid?>(), Arg.Any<IReadOnlyList<PrCommentThread>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_JobCancelledAfterFileReview_DiscardsResultNoCommentPost()
    {
        // T012 (c): job status flips to Cancelled between file-review and synthesis → no comment posted
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, instructionFetcher, instructionEvaluator, logger) =
            CreateDeps();

        var job = CreateJob();
        var pr = CreatePullRequest(new List<PrCommentThread>());

        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(pr);

        var reviewResult = new ReviewResult("A thorough review summary.", new List<ReviewComment>().AsReadOnly());
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(reviewResult));

        // Pre-review checkpoint: GetById returns non-Cancelled (let pre-check pass)
        // Post-review checkpoint: GetById returns Cancelled
        var normalJob = new ReviewJob(job.Id, job.ClientId, job.OrganizationUrl, job.ProjectId, job.RepositoryId, job.PullRequestId, job.IterationId);
        var cancelledJob = new ReviewJob(job.Id, job.ClientId, job.OrganizationUrl, job.ProjectId, job.RepositoryId, job.PullRequestId, job.IterationId);
        cancelledJob.Status = JobStatus.Cancelled;
        // First call (pre-review check) returns the normal job; second call (post-review check) returns Cancelled
        jobs.GetById(job.Id).Returns(normalJob, cancelledJob);

        var sut = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        // Act
        await sut.ProcessAsync(job, CancellationToken.None);

        // Assert: comment NEVER posted; result not committed
        await commentPoster.DidNotReceive().PostAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ReviewResult>(),
            Arg.Any<Guid?>(), Arg.Any<IReadOnlyList<PrCommentThread>>(), Arg.Any<CancellationToken>());
        await jobs.DidNotReceive().SetResultAsync(Arg.Any<Guid>(), Arg.Any<ReviewResult>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_CompletedPr_DoesNotCallSetCancelledAsync()
    {
        // T012 (d): pr.Status == Completed → SetCancelledAsync NOT called (only Abandoned triggers cancellation)
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, instructionFetcher, instructionEvaluator, logger) =
            CreateDeps();

        var job = CreateJob();
        var completedPr = CreatePullRequest() with { Status = PrStatus.Completed };

        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(completedPr);

        var sut = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        // Act
        await sut.ProcessAsync(job, CancellationToken.None);

        // Assert: SetCancelledAsync never called (completed PRs use SetFailedAsync or are silently skipped)
        await jobs.DidNotReceive().SetCancelledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // --- T022: US2 Incremental Review tests (failing until T024 is implemented) ---

    /// <summary>
    /// Helper: sets up a prior completed ReviewJob for iteration <paramref name="iteration"/>
    /// with completed file results for each path in <paramref name="priorFilePaths"/>.
    /// </summary>
    private static ReviewJob BuildPriorJob(ReviewJob currentJob, int iteration, params string[] priorFilePaths)
    {
        var priorJobId = Guid.NewGuid();
        var priorJob = new ReviewJob(priorJobId, currentJob.ClientId, currentJob.OrganizationUrl,
            currentJob.ProjectId, currentJob.RepositoryId, currentJob.PullRequestId, iteration);
        foreach (var path in priorFilePaths)
        {
            var result = new ReviewFileResult(priorJobId, path);
            result.MarkCompleted($"summary for {path}", new List<ReviewComment>().AsReadOnly());
            priorJob.FileReviewResults.Add(result);
        }
        return priorJob;
    }

    [Fact]
    public async Task ProcessAsync_PriorCompletedJobExists_CarriesForwardUnchangedFiles()
    {
        // T022 (a): prior Completed job exists → GetCompletedJobWithFileResultsAsync called,
        // carried-forward results persisted for unchanged files, AI calls for delta only
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _, logger) =
            CreateDeps();

        // Job is for iteration 2; prior iteration was 1
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 2);
        var changedFile = new ChangedFile("src/Changed.cs", ChangeType.Edit, "new content", "diff");
        var pr = new PullRequest(
            job.OrganizationUrl, job.ProjectId, job.RepositoryId,
            job.RepositoryId, job.PullRequestId, job.IterationId, "Test PR", null, "feature/x", "main",
            new List<ChangedFile> { changedFile }.AsReadOnly(),
            PrStatus.Active, null);

        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(pr);
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(CreateReviewResult());

        // Scan shows prior iteration was 1
        var scan = new ReviewPrScan(Guid.NewGuid(), job.ClientId, job.RepositoryId, job.PullRequestId, "1");
        prScanRepository.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(scan));

        // Prior job (iteration 1) has both Changed.cs and Unchanged.cs
        var priorJob = BuildPriorJob(job, 1, "src/Changed.cs", "src/Unchanged.cs");
        jobs.GetCompletedJobWithFileResultsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), 1, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(priorJob));

        var sut = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        // Act
        await sut.ProcessAsync(job, CancellationToken.None);

        // Assert 1: carry-forward result persisted for the unchanged file only
        await jobs.Received(1).AddFileResultAsync(
            Arg.Is<ReviewFileResult>(r => r.IsCarriedForward && r.FilePath == "src/Unchanged.cs"),
            Arg.Any<CancellationToken>());

        // Assert 2: carry-forward was NOT created for the changed file
        await jobs.DidNotReceive().AddFileResultAsync(
            Arg.Is<ReviewFileResult>(r => r.IsCarriedForward && r.FilePath == "src/Changed.cs"),
            Arg.Any<CancellationToken>());

        // Assert 3: AI orchestrator was still called (for the delta file Changed.cs)
        await orchestrator.Received(1).ReviewAsync(
            Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_NoPriorCompletedJob_ReviewsAllFilesNormally()
    {
        // T022 (b): no prior job → no carry-forward, all files reviewed in full
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _, logger) =
            CreateDeps();

        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 2);
        var changedFile = new ChangedFile("src/AFile.cs", ChangeType.Edit, "content", "diff");
        var pr = new PullRequest(
            job.OrganizationUrl, job.ProjectId, job.RepositoryId,
            job.RepositoryId, job.PullRequestId, job.IterationId, "Test PR", null, "feature/x", "main",
            new List<ChangedFile> { changedFile }.AsReadOnly(),
            PrStatus.Active, null);

        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(pr);
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(CreateReviewResult());

        // Scan shows prior iteration 1 (so compareToIterationId will be 1)
        var scan = new ReviewPrScan(Guid.NewGuid(), job.ClientId, job.RepositoryId, job.PullRequestId, "1");
        prScanRepository.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(scan));

        // No prior completed job
        jobs.GetCompletedJobWithFileResultsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(null));

        var sut = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        // Act
        await sut.ProcessAsync(job, CancellationToken.None);

        // Assert: no carry-forward files added
        await jobs.DidNotReceive().AddFileResultAsync(
            Arg.Is<ReviewFileResult>(r => r.IsCarriedForward),
            Arg.Any<CancellationToken>());

        // Assert: AI orchestrator was called normally
        await orchestrator.Received(1).ReviewAsync(
            Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_EmptyDeltaWithPriorJob_DeletesJobWithoutAiCalls()
    {
        // T022 (c): delta is empty (no changed files) but prior job exists →
        //           job deleted, no AI calls, all prior results carried forward
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _, logger) =
            CreateDeps();

        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 2);
        // PR has NO changed files
        var pr = new PullRequest(
            job.OrganizationUrl, job.ProjectId, job.RepositoryId,
            job.RepositoryId, job.PullRequestId, job.IterationId, "Test PR", null, "feature/x", "main",
            new List<ChangedFile>().AsReadOnly(),
            PrStatus.Active, null);

        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(pr);

        // Scan shows prior iteration 1
        var scan = new ReviewPrScan(Guid.NewGuid(), job.ClientId, job.RepositoryId, job.PullRequestId, "1");
        prScanRepository.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(scan));

        // Prior job has files (all will be carried forward since delta is empty)
        var priorJob = BuildPriorJob(job, 1, "src/Alpha.cs", "src/Beta.cs");
        jobs.GetCompletedJobWithFileResultsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(priorJob));

        var sut = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        // Act
        await sut.ProcessAsync(job, CancellationToken.None);

        // Assert: AI orchestrator was NOT called (nothing new to review)
        await orchestrator.DidNotReceive().ReviewAsync(
            Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>());

        // Assert: job was deleted (no new comments to post)
        await jobs.Received(1).DeleteAsync(job.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_PriorJobExists_CarriedForwardPathsPopulatedInResult()
    {
        // T022 (d)/(e): synthesis receives carried-forward entries;
        //               ReviewResult.CarriedForwardFilePaths populated correctly
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _, logger) =
            CreateDeps();

        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 2);
        var changedFile = new ChangedFile("src/Changed.cs", ChangeType.Edit, "content", "diff");
        var pr = new PullRequest(
            job.OrganizationUrl, job.ProjectId, job.RepositoryId,
            job.RepositoryId, job.PullRequestId, job.IterationId, "Test PR", null, "feature/x", "main",
            new List<ChangedFile> { changedFile }.AsReadOnly(),
            PrStatus.Active, null);

        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(pr);
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(CreateReviewResult());

        var scan = new ReviewPrScan(Guid.NewGuid(), job.ClientId, job.RepositoryId, job.PullRequestId, "1");
        prScanRepository.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(scan));

        // Prior job: Changed.cs and Unchanged.cs; only Unchanged.cs will be carried forward
        var priorJob = BuildPriorJob(job, 1, "src/Changed.cs", "src/Unchanged.cs");
        jobs.GetCompletedJobWithFileResultsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(priorJob));

        var sut = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        // Act
        await sut.ProcessAsync(job, CancellationToken.None);

        // Assert: SetResultAsync was called with CarriedForwardFilePaths containing the unchanged file
        await jobs.Received(1).SetResultAsync(
            job.Id,
            Arg.Is<ReviewResult>(r => r.CarriedForwardFilePaths.Contains("src/Unchanged.cs")),
            Arg.Any<CancellationToken>());

        // Assert: the carried-forward path is NOT in the delta list (Changed.cs is the delta)
        await jobs.Received(1).SetResultAsync(
            job.Id,
            Arg.Is<ReviewResult>(r => !r.CarriedForwardFilePaths.Contains("src/Changed.cs")),
            Arg.Any<CancellationToken>());
    }
}
