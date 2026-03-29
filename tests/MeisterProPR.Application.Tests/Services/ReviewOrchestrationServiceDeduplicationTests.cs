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

namespace MeisterProPR.Application.Tests.Services;

/// <summary>
///     Tests asserting that <see cref="ReviewOrchestrationService" /> passes through
///     the orchestrator result without applying additional deduplication (US2 is now
///     handled inside <see cref="IFileByFileReviewOrchestrator" />, T024).
/// </summary>
public class ReviewOrchestrationServiceDeduplicationTests
{
    private static ReviewOrchestrationService CreateService(
        IJobRepository jobs,
        IPullRequestFetcher prFetcher,
        IFileByFileReviewOrchestrator orchestrator,
        IAdoCommentPoster commentPoster,
        IClientRegistry clientRegistry,
        IReviewPrScanRepository prScanRepository)
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
            Substitute.For<ILogger<ReviewOrchestrationService>>());
    }

    // T024 — ReviewOrchestrationService passes the orchestrator result to the comment poster
    //         without additional deduplication (dedup was moved to FileByFileReviewOrchestrator)
    [Fact]
    public async Task ProcessAsync_PassesOrchestratorResultToCommentPosterUnmodified()
    {
        // Arrange
        var jobs = Substitute.For<IJobRepository>();
        var prFetcher = Substitute.For<IPullRequestFetcher>();
        var orchestrator = Substitute.For<IFileByFileReviewOrchestrator>();
        var commentPoster = Substitute.For<IAdoCommentPoster>();
        var clientRegistry = Substitute.For<IClientRegistry>();
        var prScanRepository = Substitute.For<IReviewPrScanRepository>();

        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
        var reviewerId = Guid.NewGuid();

        prScanRepository.GetAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewPrScan?>(null));
        clientRegistry.GetReviewerIdAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Guid?>(reviewerId));
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

        // The orchestrator returns two comments on different files
        var twoComments = new List<ReviewComment>
        {
            new("src/A.cs", 1, CommentSeverity.Warning, "Use IDisposable pattern here"),
            new("src/B.cs", 2, CommentSeverity.Warning, "Use IDisposable pattern here"),
        }.AsReadOnly();

        var orchestratorResult = new ReviewResult("Summary with two comments.", twoComments);
        orchestrator.ReviewAsync(
                Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(),
                Arg.Any<ReviewSystemContext>(), Arg.Any<CancellationToken>())
            .Returns(orchestratorResult);

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, clientRegistry, prScanRepository);

        // Act
        await service.ProcessAsync(job, CancellationToken.None);

        // Assert: commentPoster receives the result exactly as returned by the orchestrator
        // (no additional deduplication happens in the service layer — it was moved to the orchestrator)
        await commentPoster.Received(1).PostAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<int>(), Arg.Any<int>(),
            Arg.Is<ReviewResult>(r => r.Comments.Count == 2),
            Arg.Any<Guid?>(), Arg.Any<IReadOnlyList<PrCommentThread>?>(),
            Arg.Any<CancellationToken>());
    }
}
