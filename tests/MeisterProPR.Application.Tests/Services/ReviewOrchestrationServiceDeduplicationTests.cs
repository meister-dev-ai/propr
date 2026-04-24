// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Diagnostics.Ports;
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

namespace MeisterProPR.Application.Tests.Services;

/// <summary>
///     Tests asserting that <see cref="ReviewOrchestrationService" /> passes through
///     the orchestrator result without applying additional deduplication (US2 is now
///     handled inside <see cref="IFileByFileReviewOrchestrator" />, T024).
/// </summary>
public class ReviewOrchestrationServiceDeduplicationTests
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
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var result = call.Arg<ReviewResult>();
                return Task.FromResult(
                    ReviewCommentPostingDiagnosticsDto.Empty(
                        result.Comments.Count + result.CarriedForwardCandidatesSkipped,
                        result.CarriedForwardCandidatesSkipped));
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

    private static IScmProviderRegistry CreateProviderRegistry(ICodeReviewPublicationService commentPoster)
    {
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
        IReviewPrScanRepository prScanRepository)
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
        var connDto = AiConnectionTestFactory.CreateChatConnection(Guid.NewGuid(), modelId: "gpt-4o");
        aiRepo.GetActiveForClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AiConnectionDto?>(connDto));
        var providerRegistry = CreateProviderRegistry(commentPoster);

        return new ReviewOrchestrationService(
            jobs,
            prFetcher,
            orchestrator,
            providerRegistry,
            clientRegistry,
            prScanRepository,
            resolutionCore,
            Substitute.For<IReviewProtocolRecorder>(),
            reviewContextToolsFactory,
            instructionFetcher,
            exclusionFetcher,
            instructionEvaluator,
            Substitute.For<IOptions<AiReviewOptions>>(),
            Substitute.For<ILogger<ReviewOrchestrationService>>(),
            aiRepo,
            Substitute.For<IAiChatClientFactory>());
    }

    // T024 — ReviewOrchestrationService passes the orchestrator result to the comment poster
    //         without additional deduplication (dedup was moved to FileByFileReviewOrchestrator)
    [Fact]
    public async Task ProcessAsync_PassesOrchestratorResultToCommentPosterUnmodified()
    {
        // Arrange
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        var prFetcher = Substitute.For<IPullRequestFetcher>();
        var orchestrator = Substitute.For<IFileByFileReviewOrchestrator>();
        var commentPoster = CreatePublicationService();
        var clientRegistry = Substitute.For<IClientRegistry>();
        var prScanRepository = Substitute.For<IReviewPrScanRepository>();

        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
        var reviewerId = Guid.NewGuid();

        prScanRepository.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(null));
        clientRegistry.GetReviewerIdentityAsync(job.ClientId, job.ProviderHost, Arg.Any<CancellationToken>())
            .Returns(new ReviewerIdentity(job.ProviderHost, reviewerId.ToString("D"), reviewerId.ToString("D"), reviewerId.ToString("D"), false));
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
                Arg.Any<CancellationToken>())
            .Returns(pr);

        // The orchestrator returns two comments on different files
        var twoComments = new List<ReviewComment>
        {
            new("src/A.cs", 1, CommentSeverity.Warning, "Use IDisposable pattern here"),
            new("src/B.cs", 2, CommentSeverity.Warning, "Use IDisposable pattern here"),
        }.AsReadOnly();

        var orchestratorResult = new ReviewResult("Summary with two comments.", twoComments);
        orchestrator.ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(orchestratorResult);
        commentPoster.PublishReviewAsync(
                Arg.Any<Guid>(),
                Arg.Any<CodeReviewRef>(),
                Arg.Any<ReviewRevision>(),
                Arg.Any<ReviewResult>(),
                Arg.Any<ReviewerIdentity>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ReviewCommentPostingDiagnosticsDto.Empty(twoComments.Count)));

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, clientRegistry, prScanRepository);

        // Act
        await service.ProcessAsync(job, CancellationToken.None);

        // Assert: commentPoster receives the result exactly as returned by the orchestrator
        // (no additional deduplication happens in the service layer — it was moved to the orchestrator)
        await commentPoster.Received(1)
            .PublishReviewAsync(
                job.ClientId,
                job.CodeReviewReference,
                Arg.Any<ReviewRevision>(),
                Arg.Is<ReviewResult>(r => r.Comments.Count == 2),
                Arg.Any<ReviewerIdentity>(),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_CarriedForwardCandidatesRemainSuppressedDuringPublication()
    {
        var jobs = Substitute.For<IReviewJobExecutionStore>();
        var prFetcher = Substitute.For<IPullRequestFetcher>();
        var orchestrator = Substitute.For<IFileByFileReviewOrchestrator>();
        var commentPoster = CreatePublicationService();
        var clientRegistry = Substitute.For<IClientRegistry>();
        var prScanRepository = Substitute.For<IReviewPrScanRepository>();

        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
        var reviewerId = Guid.NewGuid();

        prScanRepository.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(null));
        clientRegistry.GetReviewerIdentityAsync(job.ClientId, job.ProviderHost, Arg.Any<CancellationToken>())
            .Returns(new ReviewerIdentity(job.ProviderHost, reviewerId.ToString("D"), reviewerId.ToString("D"), reviewerId.ToString("D"), false));
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
            "Incremental PR",
            null,
            "feature/x",
            "main",
            [new ChangedFile("src/Fresh.cs", ChangeType.Edit, "content", "diff")]);

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

        var orchestratorResult = new ReviewResult("Incremental summary", [])
        {
            CarriedForwardCandidatesSkipped = 2,
            CarriedForwardFilePaths = ["src/Legacy.cs"],
        };

        orchestrator.ReviewAsync(
                Arg.Any<ReviewJob>(),
                Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<IChatClient?>())
            .Returns(orchestratorResult);

        commentPoster.PublishReviewAsync(
                Arg.Any<Guid>(),
                Arg.Any<CodeReviewRef>(),
                Arg.Any<ReviewRevision>(),
                Arg.Any<ReviewResult>(),
                Arg.Any<ReviewerIdentity>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(ReviewCommentPostingDiagnosticsDto.Empty(2, 2)));

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, clientRegistry, prScanRepository);
        var expectedCarriedForwardPaths = new[] { "src/Legacy.cs" };

        await service.ProcessAsync(job, CancellationToken.None);

        await commentPoster.Received(1)
            .PublishReviewAsync(
                job.ClientId,
                job.CodeReviewReference,
                Arg.Any<ReviewRevision>(),
                Arg.Is<ReviewResult>(result =>
                    result.Comments.Count == 0 &&
                    result.CarriedForwardCandidatesSkipped == 2 &&
                    result.CarriedForwardFilePaths.SequenceEqual(expectedCarriedForwardPaths)),
                Arg.Any<ReviewerIdentity>(),
                Arg.Any<CancellationToken>());
    }
}
