// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

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

/// <summary>
///     Tests asserting that <see cref="ReviewOrchestrationService" /> loads prompt overrides from
///     <see cref="IPromptOverrideService" /> and populates <see cref="ReviewSystemContext.PromptOverrides" />
///     before dispatching to the orchestrator.
/// </summary>
public class ReviewOrchestrationServicePromptOverrideTests
{
    private static ReviewOrchestrationService CreateService(
        IJobRepository jobs,
        IPullRequestFetcher prFetcher,
        IFileByFileReviewOrchestrator orchestrator,
        IAdoCommentPoster commentPoster,
        IClientRegistry clientRegistry,
        IReviewPrScanRepository prScanRepository,
        IPromptOverrideService promptOverrideService)
    {
        var reviewerManager = Substitute.For<IAdoReviewerManager>();
        var threadClient = Substitute.For<IAdoThreadClient>();
        var threadReplier = Substitute.For<IAdoThreadReplier>();
        var resolutionCore = Substitute.For<IAiCommentResolutionCore>();
        var reviewContextToolsFactory = Substitute.For<IReviewContextToolsFactory>();
        reviewContextToolsFactory
            .Create(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<Guid?>())
            .Returns(Substitute.For<IReviewContextTools>());

        var instructionFetcher = Substitute.For<IRepositoryInstructionFetcher>();
        instructionFetcher
            .FetchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RepositoryInstruction>>([]));

        var instructionEvaluator = Substitute.For<IRepositoryInstructionEvaluator>();
        instructionEvaluator
            .EvaluateRelevanceAsync(Arg.Any<IReadOnlyList<RepositoryInstruction>>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RepositoryInstruction>>([]));

        var exclusionFetcher = Substitute.For<IRepositoryExclusionFetcher>();
        exclusionFetcher
            .FetchAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ReviewExclusionRules.Empty));

        var aiRepo = Substitute.For<IAiConnectionRepository>();
        var connDto = new AiConnectionDto(Guid.NewGuid(), Guid.NewGuid(), "Test Connection", "https://api.test.com/", ["gpt-4o"], IsActive: true, ActiveModel: "gpt-4o", CreatedAt: DateTimeOffset.UtcNow);
        aiRepo.GetActiveForClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AiConnectionDto?>(connDto));

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
            instructionFetcher,
            exclusionFetcher,
            instructionEvaluator,
            Substitute.For<IOptions<AiReviewOptions>>(),
            Substitute.For<ILogger<ReviewOrchestrationService>>(),
            aiRepo,
            Substitute.For<IAiChatClientFactory>(),
            promptOverrideService: promptOverrideService);
    }

    private static (ReviewJob job, IPullRequestFetcher prFetcher, IClientRegistry clientRegistry, IReviewPrScanRepository prScanRepository) BuildDefaults()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
        var prFetcher = Substitute.For<IPullRequestFetcher>();
        var clientRegistry = Substitute.For<IClientRegistry>();
        var prScanRepository = Substitute.For<IReviewPrScanRepository>();

        prScanRepository.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(null));
        clientRegistry.GetReviewerIdAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Guid?>(Guid.NewGuid()));
        clientRegistry.GetCommentResolutionBehaviorAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CommentResolutionBehavior.Silent));
        clientRegistry.GetCustomSystemMessageAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var pr = new PullRequest(
            job.OrganizationUrl, job.ProjectId, job.RepositoryId,
            job.RepositoryId, job.PullRequestId, job.IterationId, "Test PR", null, "feature/x", "main",
            new List<ChangedFile>().AsReadOnly(), PrStatus.Active, null);

        prFetcher.FetchAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(pr);

        return (job, prFetcher, clientRegistry, prScanRepository);
    }

    [Fact]
    public async Task ProcessAsync_WhenOverrideExistsForKey_PopulatesPromptOverridesOnContext()
    {
        // Arrange
        var (job, prFetcher, clientRegistry, prScanRepository) = BuildDefaults();

        var promptOverrideService = Substitute.For<IPromptOverrideService>();
        promptOverrideService
            .GetOverrideAsync(job.ClientId, null, "SynthesisSystemPrompt", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>("Custom synthesis instructions"));
        promptOverrideService
            .GetOverrideAsync(job.ClientId, null, Arg.Is<string>(k => k != "SynthesisSystemPrompt"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var orchestrator = Substitute.For<IFileByFileReviewOrchestrator>();
        ReviewSystemContext? capturedContext = null;
        orchestrator
            .ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(),
                Arg.Do<ReviewSystemContext>(ctx => capturedContext = ctx),
                Arg.Any<CancellationToken>(),
                Arg.Any<Microsoft.Extensions.AI.IChatClient?>())
            .Returns(new ReviewResult("Summary", new List<ReviewComment>().AsReadOnly()));

        var service = CreateService(
            Substitute.For<IJobRepository>(), prFetcher, orchestrator,
            Substitute.For<IAdoCommentPoster>(), clientRegistry, prScanRepository,
            promptOverrideService);

        // Act
        await service.ProcessAsync(job, CancellationToken.None);

        // Assert
        Assert.NotNull(capturedContext);
        Assert.True(capturedContext!.PromptOverrides.ContainsKey("SynthesisSystemPrompt"));
        Assert.Equal("Custom synthesis instructions", capturedContext.PromptOverrides["SynthesisSystemPrompt"]);
    }

    [Fact]
    public async Task ProcessAsync_WhenNoOverridesExist_PromptOverridesIsEmpty()
    {
        // Arrange
        var (job, prFetcher, clientRegistry, prScanRepository) = BuildDefaults();

        var promptOverrideService = Substitute.For<IPromptOverrideService>();
        promptOverrideService
            .GetOverrideAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var orchestrator = Substitute.For<IFileByFileReviewOrchestrator>();
        ReviewSystemContext? capturedContext = null;
        orchestrator
            .ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(),
                Arg.Do<ReviewSystemContext>(ctx => capturedContext = ctx),
                Arg.Any<CancellationToken>(),
                Arg.Any<Microsoft.Extensions.AI.IChatClient?>())
            .Returns(new ReviewResult("Summary", new List<ReviewComment>().AsReadOnly()));

        var service = CreateService(
            Substitute.For<IJobRepository>(), prFetcher, orchestrator,
            Substitute.For<IAdoCommentPoster>(), clientRegistry, prScanRepository,
            promptOverrideService);

        // Act
        await service.ProcessAsync(job, CancellationToken.None);

        // Assert
        Assert.NotNull(capturedContext);
        Assert.Empty(capturedContext!.PromptOverrides);
    }

    [Fact]
    public async Task ProcessAsync_QueriesEachValidPromptKey_OncePerKey()
    {
        // Arrange
        var (job, prFetcher, clientRegistry, prScanRepository) = BuildDefaults();

        var promptOverrideService = Substitute.For<IPromptOverrideService>();
        promptOverrideService
            .GetOverrideAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var orchestrator = Substitute.For<IFileByFileReviewOrchestrator>();
        orchestrator
            .ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(), Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>(), Arg.Any<Microsoft.Extensions.AI.IChatClient?>())
            .Returns(new ReviewResult("Summary", new List<ReviewComment>().AsReadOnly()));

        var service = CreateService(
            Substitute.For<IJobRepository>(), prFetcher, orchestrator,
            Substitute.For<IAdoCommentPoster>(), clientRegistry, prScanRepository,
            promptOverrideService);

        // Act
        await service.ProcessAsync(job, CancellationToken.None);

        // Assert — one call per valid key, always with crawlConfigId: null
        foreach (var key in PromptOverride.ValidPromptKeys)
        {
            await promptOverrideService.Received(1)
                .GetOverrideAsync(job.ClientId, null, key, Arg.Any<CancellationToken>());
        }
    }

    [Fact]
    public async Task ProcessAsync_WhenServiceThrows_ProceedsWithEmptyOverrides()
    {
        // Arrange
        var (job, prFetcher, clientRegistry, prScanRepository) = BuildDefaults();

        var promptOverrideService = Substitute.For<IPromptOverrideService>();
        promptOverrideService
            .GetOverrideAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB unavailable"));

        var orchestrator = Substitute.For<IFileByFileReviewOrchestrator>();
        ReviewSystemContext? capturedContext = null;
        orchestrator
            .ReviewAsync(Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(),
                Arg.Do<ReviewSystemContext>(ctx => capturedContext = ctx),
                Arg.Any<CancellationToken>(),
                Arg.Any<Microsoft.Extensions.AI.IChatClient?>())
            .Returns(new ReviewResult("Summary", new List<ReviewComment>().AsReadOnly()));

        var service = CreateService(
            Substitute.For<IJobRepository>(), prFetcher, orchestrator,
            Substitute.For<IAdoCommentPoster>(), clientRegistry, prScanRepository,
            promptOverrideService);

        // Act — must not throw
        await service.ProcessAsync(job, CancellationToken.None);

        // Assert — review still runs but with empty overrides
        Assert.NotNull(capturedContext);
        Assert.Empty(capturedContext!.PromptOverrides);
    }
}
