// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Options;
using MeisterProPR.Application.Services;
using MeisterProPR.Application.ValueObjects;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
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
    private static ICodeReviewPublicationService CreatePublicationService()
    {
        var publicationService = Substitute.For<ICodeReviewPublicationService>();
        publicationService.Provider.Returns(ScmProvider.AzureDevOps);
        publicationService.PublishReviewAsync(
                Arg.Any<Guid>(),
                Arg.Any<CodeReviewRef>(),
                Arg.Any<ReviewRevision>(),
                Arg.Any<ReviewResult>(),
                Arg.Any<ReviewerIdentity>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<ReviewPublicationContext?>())
            .Returns(Task.FromResult(ReviewCommentPostingDiagnosticsDto.Empty()));
        return publicationService;
    }

    private static IReviewAssignmentService CreateReviewerManager()
    {
        var reviewerManager = Substitute.For<IReviewAssignmentService>();
        reviewerManager.Provider.Returns(ScmProvider.AzureDevOps);
        reviewerManager.AddOptionalReviewerAsync(
                Arg.Any<Guid>(),
                Arg.Any<CodeReviewRef>(),
                Arg.Any<ReviewerIdentity>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return reviewerManager;
    }

    private static IReviewThreadStatusWriter CreateThreadStatusWriter()
    {
        var threadStatusWriter = Substitute.For<IReviewThreadStatusWriter>();
        threadStatusWriter.Provider.Returns(ScmProvider.AzureDevOps);
        threadStatusWriter.UpdateThreadStatusAsync(
                Arg.Any<Guid>(),
                Arg.Any<ReviewThreadRef>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return threadStatusWriter;
    }

    private static IReviewThreadReplyPublisher CreateThreadReplyPublisher()
    {
        var threadReplyPublisher = Substitute.For<IReviewThreadReplyPublisher>();
        threadReplyPublisher.Provider.Returns(ScmProvider.AzureDevOps);
        threadReplyPublisher.ReplyAsync(
                Arg.Any<Guid>(),
                Arg.Any<ReviewThreadRef>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return threadReplyPublisher;
    }

    private static IScmProviderRegistry CreateProviderRegistry(ICodeReviewPublicationService commentPoster)
    {
        var reviewerManager = CreateReviewerManager();
        var threadStatusWriter = CreateThreadStatusWriter();
        var threadReplyPublisher = CreateThreadReplyPublisher();
        var registry = Substitute.For<IScmProviderRegistry>();
        registry.GetCodeReviewPublicationService(Arg.Any<ScmProvider>()).Returns(commentPoster);
        registry.GetReviewAssignmentService(Arg.Any<ScmProvider>()).Returns(reviewerManager);
        registry.GetReviewThreadStatusWriter(Arg.Any<ScmProvider>()).Returns(threadStatusWriter);
        registry.GetReviewThreadReplyPublisher(Arg.Any<ScmProvider>()).Returns(threadReplyPublisher);
        registry.GetRegisteredCapabilities(Arg.Any<ScmProvider>())
            .Returns(
            [
                "reviewAssignment",
                "reviewThreadStatus",
                "reviewThreadReply",
            ]);
        return registry;
    }

    private static ReviewOrchestrationService CreateService(
        IReviewJobExecutionStore jobs,
        IPullRequestFetcher prFetcher,
        IFileByFileReviewOrchestrator orchestrator,
        ICodeReviewPublicationService commentPoster,
        IClientRegistry clientRegistry,
        IReviewPrScanRepository prScanRepository,
        IPromptOverrideService promptOverrideService)
    {
        var reviewerManager = CreateReviewerManager();
        var resolutionCore = Substitute.For<IAiCommentResolutionCore>();
        var reviewContextToolsFactory = Substitute.For<IReviewContextToolsFactory>();
        reviewContextToolsFactory
            .Create(Arg.Any<ReviewContextToolsRequest>())
            .Returns(Substitute.For<IReviewContextTools>());

        var instructionFetcher = Substitute.For<IRepositoryInstructionFetcher>();
        instructionFetcher
            .FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RepositoryInstruction>>([]));

        var instructionEvaluator = Substitute.For<IRepositoryInstructionEvaluator>();
        instructionEvaluator
            .EvaluateRelevanceAsync(
                Arg.Any<IReadOnlyList<RepositoryInstruction>>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RepositoryInstruction>>([]));

        var exclusionFetcher = Substitute.For<IRepositoryExclusionFetcher>();
        exclusionFetcher
            .FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ReviewExclusionRules.Empty));

        var aiRepo = Substitute.For<IAiConnectionRepository>();
        var connDto = AiConnectionTestFactory.CreateChatConnection(Guid.NewGuid());
        aiRepo.GetActiveForClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AiConnectionDto?>(connDto));
        var providerRegistry = CreateProviderRegistry(commentPoster);
        var reviewStrategyDispatcher = CreateDispatcher(orchestrator);

        return new ReviewOrchestrationService(
            jobs,
            prFetcher,
            providerRegistry,
            clientRegistry,
            prScanRepository,
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
            reviewStrategyDispatcher,
            promptOverrideService,
            workspaceManager: CreateDefaultWorkspaceManager());
    }

    private static IReviewRepositoryWorkspaceManager CreateDefaultWorkspaceManager()
    {
        var workspace = Substitute.For<IReviewRepositoryWorkspace>();
        workspace.DisposeAsync().Returns(ValueTask.CompletedTask);
        var manager = Substitute.For<IReviewRepositoryWorkspaceManager>();
        manager.PrepareAsync(Arg.Any<ReviewRepositoryWorkspaceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ReviewRepositoryWorkspacePreparationResult(workspace, null));
        return manager;
    }

    private static IReviewStrategyDispatcher CreateDispatcher(IFileByFileReviewOrchestrator orchestrator)
    {
        var dispatcher = Substitute.For<IReviewStrategyDispatcher>();
        dispatcher.ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>(),
                Arg.Any<string?>())
            .Returns(callInfo => orchestrator.ReviewAsync(
                callInfo.ArgAt<ReviewJob>(0),
                callInfo.ArgAt<PullRequest>(1),
                callInfo.ArgAt<ReviewSystemContext>(2),
                callInfo.ArgAt<CancellationToken>(3),
                callInfo.ArgAt<IChatClient?>(4)));
        return dispatcher;
    }

    private static (ReviewJob job, IPullRequestFetcher prFetcher, IClientRegistry clientRegistry,
        IReviewPrScanRepository prScanRepository) BuildDefaults()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
        job.SetReviewRevision(new ReviewRevision("head-sha", "base-sha", null, null, null));
        var prFetcher = Substitute.For<IPullRequestFetcher>();
        prFetcher.FetchRefAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PullRequestRef("feature/test", "main", PrStatus.Active)));
        var clientRegistry = Substitute.For<IClientRegistry>();
        var prScanRepository = Substitute.For<IReviewPrScanRepository>();

        prScanRepository.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(null));
        var reviewerId = Guid.NewGuid();
        var reviewerIdentity = new ReviewerIdentity(
            job.ProviderHost,
            reviewerId.ToString("D"),
            reviewerId.ToString("D"),
            reviewerId.ToString("D"),
            false);
        clientRegistry.GetReviewerIdentityAsync(job.ClientId, job.ProviderHost, Arg.Any<CancellationToken>())
            .Returns(reviewerIdentity);
        clientRegistry.GetEffectiveReviewerIdentityAsync(job.ClientId, job.ProviderHost, Arg.Any<CancellationToken>())
            .Returns(reviewerIdentity);
        clientRegistry.GetCommentResolutionBehaviorAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CommentResolutionBehavior.Silent));
        clientRegistry.GetCustomSystemMessageAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var pr = new PullRequest(
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.RepositoryId,
            job.PullRequestId,
            job.IterationId,
            "Test PR",
            null,
            "feature/x",
            "main",
            new List<ChangedFile>().AsReadOnly());

        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<ReviewRevision?>(),
                Arg.Any<IReviewRepositoryWorkspace?>())
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
            .GetOverrideAsync(
                job.ClientId,
                null,
                Arg.Is<string>(k => k != "SynthesisSystemPrompt"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var orchestrator = Substitute.For<IFileByFileReviewOrchestrator>();
        ReviewSystemContext? capturedContext = null;
        orchestrator
            .ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Do<ReviewSystemContext>(ctx => capturedContext = ctx),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(new ReviewResult("Summary", new List<ReviewComment>().AsReadOnly()));

        var service = CreateService(
            Substitute.For<IReviewJobExecutionStore>(),
            prFetcher,
            orchestrator,
            CreatePublicationService(),
            clientRegistry,
            prScanRepository,
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
            .ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Do<ReviewSystemContext>(ctx => capturedContext = ctx),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(new ReviewResult("Summary", new List<ReviewComment>().AsReadOnly()));

        var service = CreateService(
            Substitute.For<IReviewJobExecutionStore>(),
            prFetcher,
            orchestrator,
            CreatePublicationService(),
            clientRegistry,
            prScanRepository,
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
            .ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(new ReviewResult("Summary", new List<ReviewComment>().AsReadOnly()));

        var service = CreateService(
            Substitute.For<IReviewJobExecutionStore>(),
            prFetcher,
            orchestrator,
            CreatePublicationService(),
            clientRegistry,
            prScanRepository,
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
            .ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Do<ReviewSystemContext>(ctx => capturedContext = ctx),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(new ReviewResult("Summary", new List<ReviewComment>().AsReadOnly()));

        var service = CreateService(
            Substitute.For<IReviewJobExecutionStore>(),
            prFetcher,
            orchestrator,
            CreatePublicationService(),
            clientRegistry,
            prScanRepository,
            promptOverrideService);

        // Act — must not throw
        await service.ProcessAsync(job, CancellationToken.None);

        // Assert — review still runs but with empty overrides
        Assert.NotNull(capturedContext);
        Assert.Empty(capturedContext!.PromptOverrides);
    }

    [Fact]
    public async Task ProcessAsync_PassesConfiguredTriggerReviewerToAssignmentButPublishesWithAuthorizedIdentity()
    {
        var (job, prFetcher, clientRegistry, prScanRepository) = BuildDefaults();

        var configuredReviewer = new ReviewerIdentity(
            job.ProviderHost,
            Guid.NewGuid().ToString("D"),
            "configured-reviewer",
            "Configured Reviewer",
            false);
        clientRegistry.GetReviewerIdentityAsync(job.ClientId, job.ProviderHost, Arg.Any<CancellationToken>())
            .Returns(configuredReviewer);
        clientRegistry.GetEffectiveReviewerIdentityAsync(job.ClientId, job.ProviderHost, Arg.Any<CancellationToken>())
            .Returns(configuredReviewer);
        clientRegistry.GetScmCommentPostingEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var authorizedPr = new PullRequest(
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.RepositoryId,
            job.PullRequestId,
            job.IterationId,
            "Test PR",
            null,
            "feature/x",
            "main",
            new List<ChangedFile>().AsReadOnly(),
            AuthorizedIdentityName: "ado-app-bot");
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<ReviewRevision?>(),
                Arg.Any<IReviewRepositoryWorkspace?>())
            .Returns(authorizedPr);

        var publicationService = CreatePublicationService();
        var promptOverrideService = Substitute.For<IPromptOverrideService>();
        promptOverrideService
            .GetOverrideAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var orchestrator = Substitute.For<IFileByFileReviewOrchestrator>();
        orchestrator
            .ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(new ReviewResult("Summary", new List<ReviewComment>().AsReadOnly()));

        var service = CreateService(
            Substitute.For<IReviewJobExecutionStore>(),
            prFetcher,
            orchestrator,
            publicationService,
            clientRegistry,
            prScanRepository,
            promptOverrideService);

        await service.ProcessAsync(job, CancellationToken.None);

        await publicationService.Received(1)
            .PublishReviewAsync(
                job.ClientId,
                job.CodeReviewReference,
                Arg.Any<ReviewRevision>(),
                Arg.Any<ReviewResult>(),
                Arg.Is<ReviewerIdentity>(identity =>
                    identity.Login == "ado-app-bot" &&
                    identity.DisplayName == "ado-app-bot" &&
                    identity.ExternalUserId == "ado-app-bot"),
                Arg.Any<CancellationToken>(),
                Arg.Any<ReviewPublicationContext?>());
    }
}
