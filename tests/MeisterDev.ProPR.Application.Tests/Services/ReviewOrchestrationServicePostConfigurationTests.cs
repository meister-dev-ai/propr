// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Application.ValueObjects;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Services;

/// <summary>
///     Tests for the per-client post configuration applied in
///     <see cref="ReviewOrchestrationService" />: the minimum-severity publication filter and the auto-resolution
///     of freshly posted threads.
/// </summary>
public class ReviewOrchestrationServicePostConfigurationTests
{
    private const string WarningFile = "src/A.cs";
    private const string SuggestionFile = "src/B.cs";

    [Fact]
    public async Task MinimumSeverity_SuppressesBelowThresholdFromPublication_ButKeepsThemInPersistedResult()
    {
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        var clientRegistry = CreateClientRegistry(out var job);
        clientRegistry.GetMinimumSeverityToPostAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CommentSeverity.Warning));

        var orchestratorResult = new ReviewResult(
            "Summary.",
            new List<ReviewComment>
            {
                new(WarningFile, 1, CommentSeverity.Warning, "A warning to keep."),
                new(SuggestionFile, 2, CommentSeverity.Suggestion, "A suggestion to suppress."),
            }.AsReadOnly());

        var commentPoster = CreatePublicationService();
        var (service, _) = CreateService(jobs, clientRegistry, orchestratorResult, commentPoster);

        await service.ProcessAsync(job, CancellationToken.None);

        // Only the Warning finding reaches the SCM publication adapter.
        await commentPoster.Received(1).PublishReviewAsync(
            job.ClientId,
            job.CodeReviewReference,
            Arg.Any<ReviewRevision>(),
            Arg.Is<ReviewResult>(result =>
                result.Comments.Count == 1 && result.Comments[0].Severity == CommentSeverity.Warning),
            Arg.Any<ReviewerIdentity>(),
            Arg.Any<CancellationToken>(),
            Arg.Any<ReviewPublicationContext?>());

        // The persisted review result still holds BOTH findings — a suppressed finding is not a discarded one.
        await jobs.Received(1).SetResultAsync(
            job.Id,
            Arg.Is<ReviewResult>(result => result.Comments.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AutoResolve_ResolvesSelectedSeverityThreadWithNote_AndLeavesOthersActive()
    {
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        var clientRegistry = CreateClientRegistry(out var job);
        clientRegistry.GetAutoResolveSeveritiesAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CommentSeverity>>([CommentSeverity.Warning]));

        var orchestratorResult = new ReviewResult(
            "Summary.",
            new List<ReviewComment>
            {
                new(WarningFile, 1, CommentSeverity.Warning, "A warning to auto-resolve."),
                new(SuggestionFile, 2, CommentSeverity.Suggestion, "A suggestion to leave active."),
            }.AsReadOnly());

        // The publication adapter reports the two threads it created, anchored to each finding.
        var commentPoster = CreatePublicationService(result => ReviewCommentPostingDiagnosticsDto
                .Empty(result.Comments.Count) with
            {
                PostedComments =
                [
                    new PostedReviewCommentRef("c1", "thread-warning", WarningFile, 1),
                    new PostedReviewCommentRef("c2", "thread-suggestion", SuggestionFile, 2),
                ],
            });

        var (service, providerRegistry) = CreateService(jobs, clientRegistry, orchestratorResult, commentPoster);

        await service.ProcessAsync(job, CancellationToken.None);

        var replyPublisher = providerRegistry.GetReviewThreadReplyPublisher(ScmProvider.AzureDevOps);
        var statusWriter = providerRegistry.GetReviewThreadStatusWriter(ScmProvider.AzureDevOps);

        // The Warning thread gets the explanatory note and is resolved.
        await replyPublisher.Received(1).ReplyAsync(
            job.ClientId,
            Arg.Is<ReviewThreadRef>(thread => thread.ExternalThreadId == "thread-warning"),
            Arg.Is<string>(note => note.Contains("Auto-resolved", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await statusWriter.Received(1).UpdateThreadStatusAsync(
            job.ClientId,
            Arg.Is<ReviewThreadRef>(thread => thread.ExternalThreadId == "thread-warning"),
            "fixed",
            Arg.Any<CancellationToken>());

        // The Suggestion thread (not in the auto-resolve set) is left untouched.
        await statusWriter.DidNotReceive().UpdateThreadStatusAsync(
            Arg.Any<Guid>(),
            Arg.Is<ReviewThreadRef>(thread => thread.ExternalThreadId == "thread-suggestion"),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DefaultConfiguration_PostsEveryFinding_AndAutoResolvesNothing()
    {
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        var clientRegistry = CreateClientRegistry(out var job);

        // Leave min-severity and auto-resolve unconfigured: the substitute defaults to Info / empty — today's behavior.
        var orchestratorResult = new ReviewResult(
            "Summary.",
            new List<ReviewComment>
            {
                new(WarningFile, 1, CommentSeverity.Warning, "A warning."),
                new(SuggestionFile, 2, CommentSeverity.Suggestion, "A suggestion."),
            }.AsReadOnly());

        var commentPoster = CreatePublicationService(result => ReviewCommentPostingDiagnosticsDto
                .Empty(result.Comments.Count) with
            {
                PostedComments =
                [
                    new PostedReviewCommentRef("c1", "thread-warning", WarningFile, 1),
                    new PostedReviewCommentRef("c2", "thread-suggestion", SuggestionFile, 2),
                ],
            });

        var (service, providerRegistry) = CreateService(jobs, clientRegistry, orchestratorResult, commentPoster);

        await service.ProcessAsync(job, CancellationToken.None);

        // Every finding is published (no severity filtering).
        await commentPoster.Received(1).PublishReviewAsync(
            job.ClientId, job.CodeReviewReference, Arg.Any<ReviewRevision>(),
            Arg.Is<ReviewResult>(result => result.Comments.Count == 2),
            Arg.Any<ReviewerIdentity>(), Arg.Any<CancellationToken>(), Arg.Any<ReviewPublicationContext?>());

        // Nothing is auto-resolved.
        var statusWriter = providerRegistry.GetReviewThreadStatusWriter(ScmProvider.AzureDevOps);
        var replyPublisher = providerRegistry.GetReviewThreadReplyPublisher(ScmProvider.AzureDevOps);
        await statusWriter.DidNotReceive().UpdateThreadStatusAsync(
            Arg.Any<Guid>(), Arg.Any<ReviewThreadRef>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await replyPublisher.DidNotReceive().ReplyAsync(Arg.Any<Guid>(), Arg.Any<ReviewThreadRef>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AutoResolve_SkipsAnchorWhereAHigherSeverityFindingSharesTheLine()
    {
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        var clientRegistry = CreateClientRegistry(out var job);
        clientRegistry.GetAutoResolveSeveritiesAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CommentSeverity>>([CommentSeverity.Suggestion]));

        // Two findings on the SAME (file, line): a Suggestion (in the set) and an Error (not in the set).
        var orchestratorResult = new ReviewResult(
            "Summary.",
            new List<ReviewComment>
            {
                new(WarningFile, 1, CommentSeverity.Suggestion, "A suggestion at line 1."),
                new(WarningFile, 1, CommentSeverity.Error, "An error at the same line."),
            }.AsReadOnly());

        var commentPoster = CreatePublicationService(result => ReviewCommentPostingDiagnosticsDto
                .Empty(result.Comments.Count) with
            {
                PostedComments =
                [
                    new PostedReviewCommentRef("c1", "thread-suggestion", WarningFile, 1),
                    new PostedReviewCommentRef("c2", "thread-error", WarningFile, 1),
                ],
            });

        var (service, providerRegistry) = CreateService(jobs, clientRegistry, orchestratorResult, commentPoster);

        await service.ProcessAsync(job, CancellationToken.None);

        // Neither thread is resolved: the Error finding sharing the anchor blocks auto-resolving the Suggestion.
        var statusWriter = providerRegistry.GetReviewThreadStatusWriter(ScmProvider.AzureDevOps);
        await statusWriter.DidNotReceive().UpdateThreadStatusAsync(
            Arg.Any<Guid>(), Arg.Any<ReviewThreadRef>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AutoResolve_ThreadResolutionFailure_DoesNotFailTheJob()
    {
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        var clientRegistry = CreateClientRegistry(out var job);
        clientRegistry.GetAutoResolveSeveritiesAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CommentSeverity>>([CommentSeverity.Warning]));

        var orchestratorResult = new ReviewResult(
            "Summary.",
            new List<ReviewComment> { new(WarningFile, 1, CommentSeverity.Warning, "A warning.") }.AsReadOnly());

        var commentPoster = CreatePublicationService(result => ReviewCommentPostingDiagnosticsDto
                .Empty(result.Comments.Count) with
            {
                PostedComments = [new PostedReviewCommentRef("c1", "thread-warning", WarningFile, 1)],
            });

        var (service, providerRegistry) = CreateService(jobs, clientRegistry, orchestratorResult, commentPoster);
        var statusWriter = providerRegistry.GetReviewThreadStatusWriter(ScmProvider.AzureDevOps);
        statusWriter.UpdateThreadStatusAsync(Arg.Any<Guid>(), Arg.Any<ReviewThreadRef>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("transient")));

        // The failing resolve must be swallowed — ProcessAsync completes and the result is still persisted.
        await service.ProcessAsync(job, CancellationToken.None);

        await jobs.Received(1).SetResultAsync(job.Id, Arg.Any<ReviewResult>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AutoResolve_UnsupportedProvider_DegradesWithoutFailingTheJob()
    {
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        var clientRegistry = CreateClientRegistry(out var job);
        clientRegistry.GetAutoResolveSeveritiesAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CommentSeverity>>([CommentSeverity.Warning]));

        var orchestratorResult = new ReviewResult(
            "Summary.",
            new List<ReviewComment> { new(WarningFile, 1, CommentSeverity.Warning, "A warning.") }.AsReadOnly());

        var commentPoster = CreatePublicationService(result => ReviewCommentPostingDiagnosticsDto
                .Empty(result.Comments.Count) with
            {
                PostedComments = [new PostedReviewCommentRef("c1", "thread-warning", WarningFile, 1)],
            });

        // resolutionSupported: false -> the registry's thread-resolution getters throw (a non-ADO provider).
        var (service, _) = CreateService(jobs, clientRegistry, orchestratorResult, commentPoster, resolutionSupported: false);

        // The unsupported provider must not fail the job; the result is still persisted.
        await service.ProcessAsync(job, CancellationToken.None);

        await jobs.Received(1).SetResultAsync(job.Id, Arg.Any<ReviewResult>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AutoResolve_ResolvesEachThreadOnce_EvenWhenAThreadYieldsMultipleCommentRefs()
    {
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        var clientRegistry = CreateClientRegistry(out var job);
        clientRegistry.GetAutoResolveSeveritiesAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CommentSeverity>>([CommentSeverity.Warning]));

        var orchestratorResult = new ReviewResult(
            "Summary.",
            new List<ReviewComment> { new(WarningFile, 1, CommentSeverity.Warning, "A warning.") }.AsReadOnly());

        // One created thread surfaces two comment refs sharing the same thread id.
        var commentPoster = CreatePublicationService(result => ReviewCommentPostingDiagnosticsDto
                .Empty(result.Comments.Count) with
            {
                PostedComments =
                [
                    new PostedReviewCommentRef("c1", "thread-warning", WarningFile, 1),
                    new PostedReviewCommentRef("c2", "thread-warning", WarningFile, 1),
                ],
            });

        var (service, providerRegistry) = CreateService(jobs, clientRegistry, orchestratorResult, commentPoster);

        await service.ProcessAsync(job, CancellationToken.None);

        // The thread is resolved and noted exactly once, not once per comment ref.
        var statusWriter = providerRegistry.GetReviewThreadStatusWriter(ScmProvider.AzureDevOps);
        var replyPublisher = providerRegistry.GetReviewThreadReplyPublisher(ScmProvider.AzureDevOps);
        await statusWriter.Received(1).UpdateThreadStatusAsync(
            job.ClientId, Arg.Is<ReviewThreadRef>(thread => thread.ExternalThreadId == "thread-warning"), "fixed", Arg.Any<CancellationToken>());
        await replyPublisher.Received(1).ReplyAsync(
            job.ClientId, Arg.Is<ReviewThreadRef>(thread => thread.ExternalThreadId == "thread-warning"), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static ICodeReviewPublicationService CreatePublicationService(Func<ReviewResult, ReviewCommentPostingDiagnosticsDto>? diagnosticsFactory = null)
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
            .Returns(call =>
            {
                var result = call.Arg<ReviewResult>();
                var diagnostics = diagnosticsFactory?.Invoke(result)
                                  ?? ReviewCommentPostingDiagnosticsDto.Empty(result.Comments.Count);
                return Task.FromResult(diagnostics);
            });
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

    private static IScmProviderRegistry CreateProviderRegistry(
        ICodeReviewPublicationService commentPoster,
        bool resolutionSupported = true)
    {
        // Build every substitute the registry hands out BEFORE wiring the .Returns() calls: creating a substitute
        // (which itself configures members) inside a .Returns() argument corrupts NSubstitute's last-call context.
        var reviewerManager = CreateReviewerManager();
        var registry = Substitute.For<IScmProviderRegistry>();
        registry.GetCodeReviewPublicationService(Arg.Any<ScmProvider>()).Returns(commentPoster);
        registry.GetReviewAssignmentService(Arg.Any<ScmProvider>()).Returns(reviewerManager);

        var threadStatusWriter = Substitute.For<IReviewThreadStatusWriter>();
        threadStatusWriter.Provider.Returns(ScmProvider.AzureDevOps);
        threadStatusWriter.UpdateThreadStatusAsync(
                Arg.Any<Guid>(),
                Arg.Any<ReviewThreadRef>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var threadReplyPublisher = Substitute.For<IReviewThreadReplyPublisher>();
        threadReplyPublisher.Provider.Returns(ScmProvider.AzureDevOps);
        threadReplyPublisher.ReplyAsync(
                Arg.Any<Guid>(),
                Arg.Any<ReviewThreadRef>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        if (resolutionSupported)
        {
            registry.GetReviewThreadStatusWriter(Arg.Any<ScmProvider>()).Returns(threadStatusWriter);
            registry.GetReviewThreadReplyPublisher(Arg.Any<ScmProvider>()).Returns(threadReplyPublisher);
        }
        else
        {
            // Mirrors a non-ADO provider whose thread-resolution adapters are not registered: the registry throws.
            registry.GetReviewThreadReplyPublisher(Arg.Any<ScmProvider>())
                .Returns(_ => throw new InvalidOperationException("No thread reply adapter registered."));
            registry.GetReviewThreadStatusWriter(Arg.Any<ScmProvider>())
                .Returns(_ => throw new InvalidOperationException("No thread status adapter registered."));
        }

        return registry;
    }

    // Builds a client registry with the identity/behavior defaults the publication path needs, and returns the job
    // it is wired for. Post-configuration getters default (Info / null) unless a test overrides them.
    private static IClientRegistry CreateClientRegistry(out ReviewJob job)
    {
        var createdJob = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
        createdJob.SetReviewRevision(new ReviewRevision("head-sha", "base-sha", null, null, null));
        job = createdJob;

        var reviewerId = Guid.NewGuid();
        var reviewerIdentity = new ReviewerIdentity(
            createdJob.ProviderHost,
            reviewerId.ToString("D"),
            reviewerId.ToString("D"),
            reviewerId.ToString("D"),
            false);

        var clientRegistry = Substitute.For<IClientRegistry>();
        clientRegistry.GetReviewerIdentityAsync(createdJob.ClientId, createdJob.ProviderHost, Arg.Any<CancellationToken>())
            .Returns(reviewerIdentity);
        clientRegistry.GetEffectiveReviewerIdentityAsync(createdJob.ClientId, createdJob.ProviderHost, Arg.Any<CancellationToken>())
            .Returns(reviewerIdentity);
        clientRegistry.GetCommentResolutionBehaviorAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CommentResolutionBehavior.Silent));
        clientRegistry.GetCustomSystemMessageAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));
        clientRegistry.GetScmCommentPostingEnabledAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        return clientRegistry;
    }

    private static (ReviewOrchestrationService Service, IScmProviderRegistry ProviderRegistry) CreateService(
        IReviewJobExecutionStore jobs,
        IClientRegistry clientRegistry,
        ReviewResult orchestratorResult,
        ICodeReviewPublicationService commentPoster,
        bool resolutionSupported = true)
    {
        var prFetcher = Substitute.For<IPullRequestFetcher>();
        prFetcher.FetchRefAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new PullRequestRef("feature/test", "main", PrStatus.Active)));

        var prScanRepository = Substitute.For<IReviewPrScanRepository>();
        prScanRepository.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(null));

        var orchestrator = Substitute.For<IFileByFileReviewOrchestrator>();
        orchestrator.ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(orchestratorResult);

        var pr = new PullRequest(
            "https://dev.azure.com/org", "proj", "repo", "repo", 1, 1,
            "Test PR", null, "feature/x", "main", new List<ChangedFile>().AsReadOnly());
        prFetcher.FetchAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>(), Arg.Any<ReviewRevision?>(), Arg.Any<IReviewRepositoryWorkspace?>())
            .Returns(pr);

        var reviewContextToolsFactory = Substitute.For<IReviewContextToolsFactory>();
        reviewContextToolsFactory.Create(Arg.Any<ReviewContextToolsRequest>())
            .Returns(Substitute.For<IReviewContextTools>());

        var instructionFetcher = Substitute.For<IRepositoryInstructionFetcher>();
        instructionFetcher.FetchAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RepositoryInstruction>>([]));

        var instructionEvaluator = Substitute.For<IRepositoryInstructionEvaluator>();
        instructionEvaluator.EvaluateRelevanceAsync(
                Arg.Any<IReadOnlyList<RepositoryInstruction>>(),
                Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<RepositoryInstruction>>([]));

        var exclusionFetcher = Substitute.For<IRepositoryExclusionFetcher>();
        exclusionFetcher.FetchAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ReviewExclusionRules.Empty));

        var connectionDto = AiConnectionTestFactory.CreateChatConnection(Guid.NewGuid());
        var aiRepo = Substitute.For<IAiConnectionRepository>();
        aiRepo.GetActiveForClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AiConnectionDto?>(connectionDto));

        var providerRegistry = CreateProviderRegistry(commentPoster, resolutionSupported);

        var service = new ReviewOrchestrationService(
            jobs,
            prFetcher,
            providerRegistry,
            clientRegistry,
            prScanRepository,
            Substitute.For<IAiCommentResolutionCore>(),
            Substitute.For<IProtocolRecorder>(),
            reviewContextToolsFactory,
            instructionFetcher,
            exclusionFetcher,
            instructionEvaluator,
            Substitute.For<IOptions<AiReviewOptions>>(),
            Substitute.For<ILogger<ReviewOrchestrationService>>(),
            aiRepo,
            Substitute.For<IAiChatClientFactory>(),
            orchestrator,
            workspaceManager: CreateDefaultWorkspaceManager());

        return (service, providerRegistry);
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
}
