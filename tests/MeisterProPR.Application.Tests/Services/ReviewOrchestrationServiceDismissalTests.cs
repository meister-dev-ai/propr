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
///     Tests asserting that <see cref="ReviewOrchestrationService" /> loads dismissals from
///     <see cref="IFindingDismissalRepository" /> and populates <see cref="ReviewSystemContext.DismissedPatterns" />
///     before dispatching to the orchestrator (US3, T023).
/// </summary>
public class ReviewOrchestrationServiceDismissalTests
{
    private static ReviewSystemContext? _capturedContext;

    private static ReviewOrchestrationService CreateService(
        IJobRepository jobs,
        IPullRequestFetcher prFetcher,
        IFileByFileReviewOrchestrator orchestrator,
        IAdoCommentPoster commentPoster,
        IClientRegistry clientRegistry,
        IReviewPrScanRepository prScanRepository,
        IFindingDismissalRepository dismissalRepository)
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
            Substitute.For<ILogger<ReviewOrchestrationService>>(),
            dismissalRepository: dismissalRepository);
    }

    // T023 — Service calls GetByClientAsync and populates DismissedPatterns with pattern texts
    [Fact]
    public async Task ProcessAsync_LoadsDismissalsAndPopulatesDismissedPatterns()
    {
        // Arrange
        var jobs = Substitute.For<IJobRepository>();
        var prFetcher = Substitute.For<IPullRequestFetcher>();
        var orchestrator = Substitute.For<IFileByFileReviewOrchestrator>();
        var commentPoster = Substitute.For<IAdoCommentPoster>();
        var clientRegistry = Substitute.For<IClientRegistry>();
        var prScanRepository = Substitute.For<IReviewPrScanRepository>();
        var dismissalRepository = Substitute.For<IFindingDismissalRepository>();

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

        var dismissal = new FindingDismissal(Guid.NewGuid(), job.ClientId, "use idisposable pattern", null, "Use IDisposable pattern here");
        dismissalRepository.GetByClientAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<FindingDismissal>>(new List<FindingDismissal> { dismissal }.AsReadOnly()));

        var pr = new PullRequest(
            job.OrganizationUrl, job.ProjectId, job.RepositoryId,
            job.RepositoryId, job.PullRequestId, job.IterationId, "Test PR", null, "feature/x", "main",
            new List<ChangedFile>().AsReadOnly(), PrStatus.Active, null);

        prFetcher.FetchAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(pr);

        ReviewSystemContext? capturedContext = null;
        orchestrator.ReviewAsync(
                Arg.Any<ReviewJob>(), Arg.Any<PullRequest>(),
                Arg.Do<ReviewSystemContext>(ctx => capturedContext = ctx),
                Arg.Any<CancellationToken>())
            .Returns(new ReviewResult("Summary", new List<ReviewComment>().AsReadOnly()));

        var service = CreateService(jobs, prFetcher, orchestrator, commentPoster, clientRegistry, prScanRepository, dismissalRepository);

        // Act
        await service.ProcessAsync(job, CancellationToken.None);

        // Assert
        await dismissalRepository.Received(1).GetByClientAsync(job.ClientId, Arg.Any<CancellationToken>());
        Assert.NotNull(capturedContext);
        Assert.Contains("use idisposable pattern", capturedContext!.DismissedPatterns);
    }
}
