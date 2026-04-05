// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;
using MeisterProPR.Application.DTOs;
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
            CreateCommentPoster(),
            Substitute.For<IAdoReviewerManager>(),
            clientRegistry,
            prScanRepository,
            instructionFetcher,
            instructionEvaluator,
            Substitute.For<ILogger<ReviewOrchestrationService>>());
    }

    private static IAdoCommentPoster CreateCommentPoster()
    {
        var commentPoster = Substitute.For<IAdoCommentPoster>();
        commentPoster.PostAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ReviewResult>(),
                Arg.Any<Guid?>(), Arg.Any<IReadOnlyList<PrCommentThread>?>(),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var result = callInfo.Arg<ReviewResult>();
                return Task.FromResult(ReviewCommentPostingDiagnosticsDto.Empty(
                    result.Comments.Count + result.CarriedForwardCandidatesSkipped,
                    result.CarriedForwardCandidatesSkipped) with
                {
                    PostedCount = result.Comments.Count,
                });
            });

        return commentPoster;
    }

    private static ReviewJob CreateJob()
    {
        return new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
    }

    private static PullRequest CreatePullRequest(IReadOnlyList<PrCommentThread>? threads = null, Guid? authorizedIdentityId = null)
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
            threads,
            AuthorizedIdentityId: authorizedIdentityId);
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
        IRepositoryExclusionFetcher? exclusionFetcher = null,
        IAiConnectionRepository? aiRepo = null,
        IAiChatClientFactory? chatFactory = null,
        IProtocolRecorder? protocolRecorder = null)
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

        var aiDependencies = aiRepo is not null && chatFactory is not null
            ? (aiRepo, chatFactory)
            : CreateAiSubstitutes();
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
            protocolRecorder ?? Substitute.For<IProtocolRecorder>(),
            reviewContextToolsFactory,
            fetcher,
            exclusionFetcherResolved,
            evaluator,
            Substitute.For<IOptions<AiReviewOptions>>(),
            logger,
                aiDependencies.aiRepo,
                aiDependencies.chatFactory);
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

    private static (IAiConnectionRepository aiRepo, IAiChatClientFactory chatFactory) CreateAiSubstitutes()
    {
        var aiRepo = Substitute.For<IAiConnectionRepository>();
        var connDto = new AiConnectionDto(Guid.NewGuid(), Guid.NewGuid(), "Test Connection", "https://api.test.com/", ["gpt-4o"], IsActive: true, ActiveModel: "gpt-4o", CreatedAt: DateTimeOffset.UtcNow);
        aiRepo.GetActiveForClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AiConnectionDto?>(connDto));

        var chatFactory = Substitute.For<IAiChatClientFactory>();
        chatFactory.CreateClient(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(Substitute.For<Microsoft.Extensions.AI.IChatClient>());

        return (aiRepo, chatFactory);
    }

    private static bool HasReviewPostingFailureDiagnostics(string? details, ReviewJob job)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return false;
        }

        using var document = JsonDocument.Parse(details);
        var root = document.RootElement;

        return root.GetProperty("operation").GetString() == "publish_review_result" &&
               root.GetProperty("jobId").GetGuid() == job.Id &&
               root.GetProperty("pullRequestId").GetInt32() == job.PullRequestId &&
               root.GetProperty("iterationId").GetInt32() == job.IterationId &&
               root.GetProperty("repositoryId").GetString() == job.RepositoryId &&
               root.GetProperty("clientId").GetGuid() == job.ClientId;
    }

    private static bool HasDedupSummaryDiagnostics(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return false;
        }

        using var document = JsonDocument.Parse(details);
        var root = document.RootElement;

        return root.GetProperty("candidateCount").GetInt32() == 2 &&
               root.GetProperty("carriedForwardCandidatesSkipped").GetInt32() == 1 &&
               root.GetProperty("usedFallbackChecks").GetBoolean();
    }

    private static bool HasDedupDegradedDiagnostics(string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return false;
        }

        using var document = JsonDocument.Parse(details);
        var root = document.RootElement;

        return root.GetProperty("cause").GetString() == "Historical duplicate protection ran without thread-memory embeddings." &&
               root.GetProperty("degradedComponents").EnumerateArray().Any(item => item.GetString() == "thread_memory_embedding") &&
               root.GetProperty("fallbackChecks").EnumerateArray().Any(item => item.GetString() == "deterministic_text_similarity") &&
               root.GetProperty("affectedCandidateCount").GetInt32() == 1 &&
               root.GetProperty("reviewContinued").GetBoolean();
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
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>())
            .Throws(new Exception("AI error"));

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        // Act
        await service.ProcessAsync(job, CancellationToken.None);

        // Assert
        await jobs.Received(1).SetFailedAsync(job.Id, Arg.Is<string>(s => s.Contains("AI error")));
        await jobs.DidNotReceive().SetResultAsync(Arg.Any<Guid>(), Arg.Any<ReviewResult>());
    }

    [Fact]
    public async Task ProcessAsync_ActiveConnectionWithoutSelectedModel_UsesFirstAvailableModel()
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

        orchestrator.ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<Microsoft.Extensions.AI.IChatClient?>())
            .Returns(CreateReviewResult());

        var aiRepo = Substitute.For<IAiConnectionRepository>();
        var activeConnection = new AiConnectionDto(
            Guid.NewGuid(),
            job.ClientId,
            "Client Connection",
            "https://api.test.com/",
            ["gpt-4.1"],
            IsActive: true,
            ActiveModel: null,
            CreatedAt: DateTimeOffset.UtcNow);
        aiRepo.GetActiveForClientAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AiConnectionDto?>(activeConnection));

        var chatClient = Substitute.For<Microsoft.Extensions.AI.IChatClient>();
        var chatFactory = Substitute.For<IAiChatClientFactory>();
        chatFactory.CreateClient(activeConnection.EndpointUrl, activeConnection.ApiKey).Returns(chatClient);

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
            instructionEvaluator,
            aiRepo: aiRepo,
            chatFactory: chatFactory);

        // Act
        await service.ProcessAsync(job, CancellationToken.None);

        // Assert
        await jobs.Received(1).UpdateAiConfigAsync(job.Id, activeConnection.Id, "gpt-4.1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_ActiveConnectionWithoutAnyAvailableModel_FailsJobBeforeReviewDispatch()
    {
        // Arrange
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, instructionFetcher, instructionEvaluator, logger) =
            CreateDeps();

        var job = CreateJob();
        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());

        var aiRepo = Substitute.For<IAiConnectionRepository>();
        aiRepo.GetActiveForClientAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AiConnectionDto?>(
                new AiConnectionDto(
                    Guid.NewGuid(),
                    job.ClientId,
                    "Broken Connection",
                    "https://api.test.com/",
                    [],
                    IsActive: true,
                    ActiveModel: null,
                    CreatedAt: DateTimeOffset.UtcNow)));

        var chatFactory = Substitute.For<IAiChatClientFactory>();

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
            instructionEvaluator,
            aiRepo: aiRepo,
            chatFactory: chatFactory);

        // Act
        await service.ProcessAsync(job, CancellationToken.None);

        // Assert
        await jobs.Received(1).SetFailedAsync(
            job.Id,
            Arg.Is<string>(message => message.Contains("has no model deployment selected", StringComparison.OrdinalIgnoreCase)),
            Arg.Any<CancellationToken>());
        await orchestrator.DidNotReceive().ReviewAsync(
            Arg.Any<ReviewJob>(),
            Arg.Any<PullRequest>(),
            Arg.Any<ReviewSystemContext>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<Microsoft.Extensions.AI.IChatClient?>());
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
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>())
            .Returns(new ReviewResult("Summary", new List<ReviewComment>().AsReadOnly()));

        var reviewContextToolsFactory = Substitute.For<IReviewContextToolsFactory>();
        reviewContextToolsFactory
            .Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<Guid?>())
            .Returns(Substitute.For<IReviewContextTools>());

        var threadClient = Substitute.For<IAdoThreadClient>();
        var threadReplier = Substitute.For<IAdoThreadReplier>();
        var resolutionCore = Substitute.For<IAiCommentResolutionCore>();

        var (aiRepo, chatFactory) = CreateAiSubstitutes();
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
            logger,
            aiRepo,
            chatFactory);

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
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>())
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
            .Returns(Task.FromResult(ReviewCommentPostingDiagnosticsDto.Empty(result.Comments.Count)))
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
        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        var protocolId = Guid.NewGuid();
        protocolRecorder.BeginAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(protocolId);

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
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>())
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

        var service = CreateService(
            jobs,
            prFetcher,
            orchestrator,
            commentPoster,
            reviewerManager,
            clientRegistry,
            prScanRepository,
            logger,
            protocolRecorder: protocolRecorder);

        // Act
        await service.ProcessAsync(job, CancellationToken.None);

        // Assert
        await jobs.Received(1).SetFailedAsync(job.Id, Arg.Is<string>(s => s.Contains("Comment post error")));
        await protocolRecorder.Received(1).RecordMemoryEventAsync(
            protocolId,
            "memory_operation_failed",
            Arg.Is<string?>(details => HasReviewPostingFailureDiagnostics(details, job)),
            Arg.Is<string?>(error => error != null && error.Contains("Failed while posting the review result", StringComparison.OrdinalIgnoreCase)),
            Arg.Any<CancellationToken>());
        await protocolRecorder.Received(1).SetCompletedAsync(protocolId, "Failed", 0, 0, 0, 0, null, Arg.Any<CancellationToken>());
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
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>())
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
    public async Task ProcessAsync_PassesResolvedThreadHistoryToCommentPoster()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _, logger) =
            CreateDeps();

        var job = CreateJob();
        var resolvedThreads = new List<PrCommentThread>
        {
            new(
                8,
                "/src/Foo.cs",
                14,
                new List<PrThreadComment>
                {
                    new("Bot", "WARNING: Previous concern.", Guid.NewGuid()),
                }.AsReadOnly(),
                "Fixed"),
        }.AsReadOnly();

        var pr = CreatePullRequest(resolvedThreads);

        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(pr);
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>())
            .Returns(CreateReviewResult());

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        await service.ProcessAsync(job, CancellationToken.None);

        await commentPoster.Received(1).PostAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<ReviewResult>(),
            Arg.Any<Guid?>(),
            Arg.Is<IReadOnlyList<PrCommentThread>?>(threads =>
                threads != null &&
                threads.Count == 1 &&
                threads[0].ThreadId == 8 &&
                threads[0].Status == "Fixed" &&
                threads[0].Comments.Count == 1 &&
                threads[0].Comments[0].Content == "WARNING: Previous concern."),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_PostingDiagnostics_RecordDedupSummaryAndDegradedMode()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _, logger) =
            CreateDeps();

        var protocolRecorder = Substitute.For<IProtocolRecorder>();
        protocolRecorder.BeginAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<AiConnectionModelCategory?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());

        var job = CreateJob();
        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());

        var pr = CreatePullRequest();
        prFetcher.FetchAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(pr);
        orchestrator.ReviewAsync(
                Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>())
            .Returns(new ReviewResult("Summary", [new ReviewComment("src/Foo.cs", 12, CommentSeverity.Warning, "Issue")])
            {
                CarriedForwardCandidatesSkipped = 1,
            });

        commentPoster.PostAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ReviewResult>(),
                Arg.Any<Guid?>(), Arg.Any<IReadOnlyList<PrCommentThread>?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ReviewCommentPostingDiagnosticsDto
            {
                CandidateCount = 2,
                PostedCount = 1,
                SuppressedCount = 1,
                CarriedForwardCandidatesSkipped = 1,
                SuppressionReasons = new Dictionary<string, int>
                {
                    ["carried_forward_source"] = 1,
                },
                ConsideredOpenThreads = true,
                ConsideredResolvedThreads = true,
                FallbackChecks = ["deterministic_text_similarity"],
                DegradedComponents = ["thread_memory_embedding"],
                DegradedCause = "Historical duplicate protection ran without thread-memory embeddings.",
                AffectedCandidateCount = 1,
            }));

        var service = CreateService(
            jobs,
            prFetcher,
            orchestrator,
            commentPoster,
            reviewerManager,
            clientRegistry,
            prScanRepository,
            logger,
            protocolRecorder: protocolRecorder);

        await service.ProcessAsync(job, CancellationToken.None);

        await protocolRecorder.Received(1).RecordDedupEventAsync(
            Arg.Any<Guid>(),
            "dedup_summary",
            Arg.Is<string?>(details => HasDedupSummaryDiagnostics(details)),
            Arg.Is<string?>(error => error == null),
            Arg.Any<CancellationToken>());

        await protocolRecorder.Received(1).RecordDedupEventAsync(
            Arg.Any<Guid>(),
            "dedup_degraded_mode",
            Arg.Is<string?>(details => HasDedupDegradedDiagnostics(details)),
            Arg.Is<string?>(error => error == null),
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
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>())
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
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>())
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
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>())
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
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>()).Returns(result);

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
        resolutionCore.EvaluateConversationalReplyAsync(Arg.Any<PrCommentThread>(), Arg.Any<Microsoft.Extensions.AI.IChatClient>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ThreadResolutionResult(false, null));

        // Build service with custom resolutionCore
        var stubToolsFactory = Substitute.For<IReviewContextToolsFactory>();
        stubToolsFactory
            .Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<Guid?>())
            .Returns(Substitute.For<IReviewContextTools>());
        var (aiRepo, chatFactory) = CreateAiSubstitutes();
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
            logger,
            aiRepo,
            chatFactory);

        await service.ProcessAsync(job, CancellationToken.None);

        // Conversational path was invoked (not code-change)
        await resolutionCore.Received(1)
            .EvaluateConversationalReplyAsync(
                Arg.Is<PrCommentThread>(t => t.ThreadId == 42),
                Arg.Any<Microsoft.Extensions.AI.IChatClient>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
        await resolutionCore.DidNotReceiveWithAnyArgs()
            .EvaluateCodeChangeAsync(null!, null!, null!, null!, Arg.Any<CancellationToken>());

        // File-by-file review must NOT run — no new commit was pushed
        await orchestrator.DidNotReceiveWithAnyArgs()
            .ReviewAsync(null!, null!, null!, Arg.Any<CancellationToken>(), null);

        // Job must be cleaned up after conversational reply
        await jobs.Received(1).DeleteAsync(job.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_SameIterationButNewRepliesOnAuthorizedIdentityThread_RunsConversationalPath()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, instructionFetcher, instructionEvaluator, logger) =
            CreateDeps();

        var reviewerId = Guid.NewGuid();
        var servicePrincipalId = Guid.NewGuid();
        var job = CreateJob(); // IterationId = 1
        var result = CreateReviewResult();
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>()).Returns(result);

        var thread = new PrCommentThread(
            84,
            "/src/Foo.cs",
            10,
            new List<PrThreadComment>
            {
                new("Bot", "Please fix this.", servicePrincipalId),
                new("Dev", "I think it's fine."),
            }.AsReadOnly());

        var pr = CreatePullRequest(new List<PrCommentThread> { thread }.AsReadOnly(), servicePrincipalId);

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

        var existingScan = new ReviewPrScan(Guid.NewGuid(), job.ClientId, job.RepositoryId, job.PullRequestId, "1");
        existingScan.Threads.Add(
            new ReviewPrScanThread
            {
                ReviewPrScanId = existingScan.Id,
                ThreadId = 84,
                LastSeenReplyCount = 0,
            });
        prScanRepository.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(existingScan));

        var resolutionCore = Substitute.For<IAiCommentResolutionCore>();
        resolutionCore.EvaluateConversationalReplyAsync(Arg.Any<PrCommentThread>(), Arg.Any<Microsoft.Extensions.AI.IChatClient>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ThreadResolutionResult(false, null));

        var stubToolsFactory = Substitute.For<IReviewContextToolsFactory>();
        stubToolsFactory
            .Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<Guid?>())
            .Returns(Substitute.For<IReviewContextTools>());
        var (aiRepo, chatFactory) = CreateAiSubstitutes();
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
            logger,
            aiRepo,
            chatFactory);

        await service.ProcessAsync(job, CancellationToken.None);

        await resolutionCore.Received(1)
            .EvaluateConversationalReplyAsync(
                Arg.Is<PrCommentThread>(t => t.ThreadId == 84),
                Arg.Any<Microsoft.Extensions.AI.IChatClient>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
        await resolutionCore.DidNotReceiveWithAnyArgs()
            .EvaluateCodeChangeAsync(null!, null!, null!, null!, Arg.Any<CancellationToken>());
        await orchestrator.DidNotReceiveWithAnyArgs()
            .ReviewAsync(null!, null!, null!, Arg.Any<CancellationToken>(), null);
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
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>()).Returns(result);

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
        resolutionCore.EvaluateCodeChangeAsync(Arg.Any<PrCommentThread>(), Arg.Any<PullRequest>(), Arg.Any<Microsoft.Extensions.AI.IChatClient>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ThreadResolutionResult(false, null));

        var stubToolsFactory2 = Substitute.For<IReviewContextToolsFactory>();
        stubToolsFactory2
            .Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<Guid?>())
            .Returns(Substitute.For<IReviewContextTools>());
        var (aiRepo2, chatFactory2) = CreateAiSubstitutes();
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
            logger,
            aiRepo2,
            chatFactory2);

        await service.ProcessAsync(job, CancellationToken.None);

        // Only the reviewer-authored thread should be evaluated
        await resolutionCore.Received(1)
            .EvaluateCodeChangeAsync(
                Arg.Is<PrCommentThread>(t => t.ThreadId == 10),
                Arg.Any<PullRequest>(),
                Arg.Any<Microsoft.Extensions.AI.IChatClient>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());

        // The other author's thread must NOT be evaluated
        await resolutionCore.DidNotReceive()
            .EvaluateCodeChangeAsync(
                Arg.Is<PrCommentThread>(t => t.ThreadId == 20),
                Arg.Any<PullRequest>(),
                Arg.Any<Microsoft.Extensions.AI.IChatClient>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_NewIteration_EvaluatesThreadsOwnedByAuthorizedIdentity()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, instructionFetcher, instructionEvaluator, logger) =
            CreateDeps();

        var reviewerId = Guid.NewGuid();
        var servicePrincipalId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var job = CreateJob(); // IterationId = 1
        var result = CreateReviewResult();
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>()).Returns(result);

        var servicePrincipalThread = new PrCommentThread(
            30,
            "/src/A.cs",
            1,
            new List<PrThreadComment>
            {
                new("Bot", "Reviewer comment", servicePrincipalId),
            }.AsReadOnly());

        var otherThread = new PrCommentThread(
            40,
            "/src/B.cs",
            2,
            new List<PrThreadComment>
            {
                new("Human", "Human comment", otherId),
            }.AsReadOnly());

        var pr = CreatePullRequest(new List<PrCommentThread> { servicePrincipalThread, otherThread }.AsReadOnly(), servicePrincipalId);

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

        prScanRepository.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(null));

        var resolutionCore = Substitute.For<IAiCommentResolutionCore>();
        resolutionCore.EvaluateCodeChangeAsync(Arg.Any<PrCommentThread>(), Arg.Any<PullRequest>(), Arg.Any<Microsoft.Extensions.AI.IChatClient>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ThreadResolutionResult(false, null));

        var stubToolsFactory = Substitute.For<IReviewContextToolsFactory>();
        stubToolsFactory
            .Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<Guid?>())
            .Returns(Substitute.For<IReviewContextTools>());
        var (aiRepo, chatFactory) = CreateAiSubstitutes();
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
            logger,
            aiRepo,
            chatFactory);

        await service.ProcessAsync(job, CancellationToken.None);

        await resolutionCore.Received(1)
            .EvaluateCodeChangeAsync(
                Arg.Is<PrCommentThread>(t => t.ThreadId == 30),
                Arg.Any<PullRequest>(),
                Arg.Any<Microsoft.Extensions.AI.IChatClient>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>());

        await resolutionCore.DidNotReceive()
            .EvaluateCodeChangeAsync(
                Arg.Is<PrCommentThread>(t => t.ThreadId == 40),
                Arg.Any<PullRequest>(),
                Arg.Any<Microsoft.Extensions.AI.IChatClient>(),
                Arg.Any<string>(),
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
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>())
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
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>())
            .Returns(result);

        ReviewSystemContext? capturedContext = null;
        orchestrator
            .When(x => x.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>()))
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
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>())
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
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>())
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
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>())
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
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>())
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
                Arg.Any<CancellationToken>(),
                Arg.Any<Microsoft.Extensions.AI.IChatClient?>());
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
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>()).Returns(result);

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

        var (aiRepoF, chatFactoryF) = CreateAiSubstitutes();
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
            logger,
            aiRepoF,
            chatFactoryF);

        await service.ProcessAsync(job, CancellationToken.None);

        // Resolution AI must not be called at all — the thread is already fixed
        await resolutionCore.DidNotReceiveWithAnyArgs()
            .EvaluateCodeChangeAsync(null!, null!, null!, null!);
        await resolutionCore.DidNotReceiveWithAnyArgs()
            .EvaluateConversationalReplyAsync(null!, null!, null!);
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
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>()).Returns(result);

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

        var (aiRepoR, chatFactoryR) = CreateAiSubstitutes();
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
            logger,
            aiRepoR,
            chatFactoryR);

        await service.ProcessAsync(job, CancellationToken.None);

        await resolutionCore.DidNotReceiveWithAnyArgs()
            .EvaluateCodeChangeAsync(null!, null!, null!, null!);
        await resolutionCore.DidNotReceiveWithAnyArgs()
            .EvaluateConversationalReplyAsync(null!, null!, null!);
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
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>())
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
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>())
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
            Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>());
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
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>())
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
            Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>());
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
        orchestrator.ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>())
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

    // ─── T068: Characterization tests act as a safety net for Phase 8 refactoring ───

    /// <summary>
    ///     T068 (a): When the PR is no longer active (completed state), ProcessAsync sets the job to
    ///     Failed without dispatching a file review or posting a comment.
    /// </summary>
    [Fact]
    public async Task T068_CharacterizationA_InactivePr_JobSetFailed_NoReviewDispatched()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _, logger) = CreateDeps();
        var job = CreateJob();

        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());

        var completedPr = new PullRequest(
            "https://dev.azure.com/org", "proj", "repo", "repo", 1, 1,
            "Completed PR", null, "feature/x", "main",
            new List<ChangedFile>().AsReadOnly(),
            PrStatus.Completed, null);

        prFetcher.FetchAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(),
                Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(completedPr);

        var sut = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        await sut.ProcessAsync(job, CancellationToken.None);

        await jobs.Received().SetFailedAsync(job.Id, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await orchestrator.DidNotReceive().ReviewAsync(
            Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(),
            Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>());
        await commentPoster.DidNotReceive().PostAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ReviewResult>(),
            Arg.Any<Guid?>(), Arg.Any<IReadOnlyList<PrCommentThread>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     T068 (b): When the PR's iteration has already been reviewed (no new commit, no new replies),
    ///     ProcessAsync deletes the job without dispatching a file review.
    /// </summary>
    [Fact]
    public async Task T068_CharacterizationB_NoNewIteration_NoNewReplies_JobDeletedWithoutReview()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _, logger) = CreateDeps();
        var job = CreateJob();
        var reviewerId = Guid.NewGuid();

        SetupReviewerIdReturns(clientRegistry, job, reviewerId);

        // Existing scan with same iteration key → no new iteration
        var existingScan = new ReviewPrScan(
            Guid.NewGuid(),
            job.ClientId,
            job.RepositoryId,
            job.PullRequestId,
            job.IterationId.ToString());

        prScanRepository.GetAsync(job.ClientId, job.RepositoryId, job.PullRequestId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(existingScan));

        // PR thread authored by reviewer with no user replies → no new replies
        var reviewerThread = new PrCommentThread(
            1001,
            null,
            null,
            new List<PrThreadComment>
            {
                new("Bot", "LGTM.", reviewerId),
            }.AsReadOnly());

        var activePr = new PullRequest(
            "https://dev.azure.com/org", "proj", "repo", "repo", 1, 1,
            "Test PR", null, "feature/x", "main",
            new List<ChangedFile>().AsReadOnly(),
            PrStatus.Active,
            [reviewerThread]);

        prFetcher.FetchAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(),
                Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(activePr);

        var sut = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        await sut.ProcessAsync(job, CancellationToken.None);

        await orchestrator.DidNotReceive().ReviewAsync(
            Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(),
            Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>());
        await jobs.Received().DeleteAsync(job.Id, Arg.Any<CancellationToken>());
        await commentPoster.DidNotReceive().PostAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ReviewResult>(),
            Arg.Any<Guid?>(), Arg.Any<IReadOnlyList<PrCommentThread>>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     T068 (c): Full review path — new scan, active PR, non-empty review result →
    ///     ProcessAsync dispatches file review, posts comment, sets job result, and saves scan.
    /// </summary>
    [Fact]
    public async Task T068_CharacterizationC_FullReviewPath_ReviewDispatchedAndResultPersisted()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _, logger) = CreateDeps();
        var job = CreateJob();

        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());

        // No existing scan → new iteration
        prScanRepository.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(null));

        var changedFile = new ChangedFile("src/Foo.cs", ChangeType.Edit, "content", "diff");
        var activePr = new PullRequest(
            "https://dev.azure.com/org", "proj", "repo", "repo", 1, 1,
            "Test PR", null, "feature/x", "main",
            [changedFile],
            PrStatus.Active, null);

        prFetcher.FetchAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(),
                Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(activePr);

        var reviewResult = new ReviewResult("Summary of review.", new List<ReviewComment>().AsReadOnly());
        orchestrator.ReviewAsync(
                Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>())
            .Returns(Task.FromResult(reviewResult));

        var sut = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        await sut.ProcessAsync(job, CancellationToken.None);

        await orchestrator.Received(1).ReviewAsync(
            Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(),
            Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>());
        await commentPoster.Received(1).PostAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ReviewResult>(),
            Arg.Any<Guid?>(), Arg.Any<IReadOnlyList<PrCommentThread>?>(), Arg.Any<CancellationToken>());
        await jobs.Received(1).SetResultAsync(job.Id, Arg.Any<ReviewResult>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     T068 (d): When reviewer identity is not configured for the client,
    ///     ProcessAsync immediately sets the job to Failed without fetching the PR.
    /// </summary>
    [Fact]
    public async Task T068_CharacterizationD_NoReviewerIdentity_JobSetFailedNoPrFetch()
    {
        var (jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, _, _, logger) = CreateDeps();
        var job = CreateJob();

        // Reviewer identity NOT configured
        clientRegistry.GetReviewerIdAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Guid?>(null));

        var sut = CreateService(jobs, prFetcher, orchestrator, commentPoster, reviewerManager, clientRegistry, prScanRepository, logger);

        await sut.ProcessAsync(job, CancellationToken.None);

        await jobs.Received(1).SetFailedAsync(job.Id, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await prFetcher.DidNotReceive().FetchAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(),
            Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
        await orchestrator.DidNotReceive().ReviewAsync(
            Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(),
            Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>());
    }
}
