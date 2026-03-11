using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.Interfaces;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterProPR.Application.Tests.Services;

public class ReviewOrchestrationServiceTests
{
    private static (
        IJobRepository jobs,
        IPullRequestFetcher prFetcher,
        IAiReviewCore aiCore,
        IAdoCommentPoster commentPoster,
        IAdoReviewerManager reviewerManager,
        IClientRegistry clientRegistry,
        ILogger<ReviewOrchestrationService> logger) CreateDeps()
    {
        return (
            Substitute.For<IJobRepository>(),
            Substitute.For<IPullRequestFetcher>(),
            Substitute.For<IAiReviewCore>(),
            Substitute.For<IAdoCommentPoster>(),
            Substitute.For<IAdoReviewerManager>(),
            Substitute.For<IClientRegistry>(),
            Substitute.For<ILogger<ReviewOrchestrationService>>());
    }

    private static ReviewJob CreateJob()
    {
        return new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 1, 1);
    }

    private static PullRequest CreatePullRequest()
    {
        return new PullRequest(
            "https://dev.azure.com/org",
            "proj",
            "repo",
            1,
            1,
            "Test PR",
            null,
            "feature/x",
            "main",
            new List<ChangedFile>().AsReadOnly());
    }

    private static ReviewResult CreateReviewResult()
    {
        return new ReviewResult("Summary", new List<ReviewComment>().AsReadOnly());
    }

    private static ReviewOrchestrationService CreateService(
        IJobRepository jobs,
        IPullRequestFetcher prFetcher,
        IAiReviewCore aiCore,
        IAdoCommentPoster commentPoster,
        IAdoReviewerManager reviewerManager,
        IClientRegistry clientRegistry,
        ILogger<ReviewOrchestrationService> logger)
    {
        return new ReviewOrchestrationService(jobs, prFetcher, aiCore, commentPoster, reviewerManager, clientRegistry, logger);
    }

    /// <summary>Set up the clientRegistry to return a non-null reviewerId for the given job's ClientId.</summary>
    private static void SetupReviewerIdReturns(IClientRegistry clientRegistry, ReviewJob job, Guid reviewerId)
    {
        if (job.ClientId is not null)
        {
            clientRegistry.GetReviewerIdAsync(job.ClientId.Value, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<Guid?>(reviewerId));
        }
    }

    [Fact]
    public async Task ProcessAsync_AiException_TransitionsJobToFailed()
    {
        // Arrange
        var (jobs, prFetcher, aiCore, commentPoster, reviewerManager, clientRegistry, logger) = CreateDeps();

        var job = CreateJob();
        var pr = CreatePullRequest();

        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<CancellationToken>())
            .Throws(new Exception("AI error"));

        var service = CreateService(jobs, prFetcher, aiCore, commentPoster, reviewerManager, clientRegistry, logger);

        // Act
        await service.ProcessAsync(job, CancellationToken.None);

        // Assert
        jobs.Received(1).SetFailed(job.Id, Arg.Is<string>(s => s.Contains("AI error")));
        jobs.DidNotReceive().SetResult(Arg.Any<Guid>(), Arg.Any<ReviewResult>());
    }

    // T025 — AddOptionalReviewerAsync is called with client's ReviewerId before PostAsync

    [Fact]
    public async Task ProcessAsync_CallsAddOptionalReviewerBeforePostAsync()
    {
        var (jobs, prFetcher, aiCore, commentPoster, reviewerManager, clientRegistry, logger) = CreateDeps();

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
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<CancellationToken>())
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
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("post"));

        var service = CreateService(jobs, prFetcher, aiCore, commentPoster, reviewerManager, clientRegistry, logger);
        await service.ProcessAsync(job, CancellationToken.None);

        Assert.Equal(["reviewer", "post"], callOrder);
    }

    [Fact]
    public async Task ProcessAsync_CommentPostException_TransitionsJobToFailed()
    {
        // Arrange
        var (jobs, prFetcher, aiCore, commentPoster, reviewerManager, clientRegistry, logger) = CreateDeps();

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
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<CancellationToken>())
            .Returns(result);
        commentPoster.PostAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<ReviewResult>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Throws(new Exception("Comment post error"));

        var service = CreateService(jobs, prFetcher, aiCore, commentPoster, reviewerManager, clientRegistry, logger);

        // Act
        await service.ProcessAsync(job, CancellationToken.None);

        // Assert
        jobs.Received(1).SetFailed(job.Id, Arg.Is<string>(s => s.Contains("Comment post error")));
    }

    [Fact]
    public async Task ProcessAsync_FetchException_TransitionsJobToFailed()
    {
        // Arrange
        var (jobs, prFetcher, aiCore, commentPoster, reviewerManager, clientRegistry, logger) = CreateDeps();

        var job = CreateJob();
        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Throws(new Exception("ADO fetch error"));

        var service = CreateService(jobs, prFetcher, aiCore, commentPoster, reviewerManager, clientRegistry, logger);

        // Act
        await service.ProcessAsync(job, CancellationToken.None);

        // Assert
        jobs.Received(1).SetFailed(job.Id, Arg.Is<string>(s => s.Contains("ADO fetch error")));
        jobs.DidNotReceive().SetResult(Arg.Any<Guid>(), Arg.Any<ReviewResult>());
    }

    // T033 — null ClientId → SetFailed immediately, no GetReviewerIdAsync call

    [Fact]
    public async Task ProcessAsync_NullClientId_CallsSetFailedImmediately()
    {
        var (jobs, prFetcher, aiCore, commentPoster, reviewerManager, clientRegistry, logger) = CreateDeps();

        // Job with null ClientId
        var job = new ReviewJob(Guid.NewGuid(), null, "https://dev.azure.com/org", "proj", "repo", 1, 1);

        var service = CreateService(jobs, prFetcher, aiCore, commentPoster, reviewerManager, clientRegistry, logger);

        await service.ProcessAsync(job, CancellationToken.None);

        jobs.Received(1).SetFailed(job.Id, Arg.Any<string>());
        await clientRegistry.DidNotReceiveWithAnyArgs().GetReviewerIdAsync(default);
    }

    // T032 — null ReviewerId → SetFailed "not configured", no reviewer call, no PostAsync

    [Fact]
    public async Task ProcessAsync_NullReviewerId_CallsSetFailedWithNotConfiguredMessage()
    {
        var (jobs, prFetcher, aiCore, commentPoster, reviewerManager, clientRegistry, logger) = CreateDeps();

        var clientId = Guid.NewGuid();
        var job = new ReviewJob(Guid.NewGuid(), clientId, "https://dev.azure.com/org", "proj", "repo", 1, 1);

        clientRegistry.GetReviewerIdAsync(clientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Guid?>(null));

        var service = CreateService(jobs, prFetcher, aiCore, commentPoster, reviewerManager, clientRegistry, logger);

        await service.ProcessAsync(job, CancellationToken.None);

        jobs.Received(1).SetFailed(job.Id, Arg.Is<string>(s => s.Contains("not configured")));
        await reviewerManager.DidNotReceiveWithAnyArgs()
            .AddOptionalReviewerAsync(default!, default!, default!, default, default);
        await commentPoster.DidNotReceiveWithAnyArgs()
            .PostAsync(default!, default!, default!, default, default, default!);
    }

    // ── EC-002: PR abandoned/closed while Processing ──────────────────────────

    [Theory]
    [InlineData(PrStatus.Abandoned)]
    [InlineData(PrStatus.Completed)]
    public async Task ProcessAsync_PrNotActive_CallsSetFailedWithoutCallingAi(PrStatus status)
    {
        // Arrange
        var (jobs, prFetcher, aiCore, commentPoster, reviewerManager, clientRegistry, logger) = CreateDeps();

        var job = CreateJob();
        var closedPr = CreatePullRequest() with { Status = status };

        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(closedPr);

        var sut = CreateService(jobs, prFetcher, aiCore, commentPoster, reviewerManager, clientRegistry, logger);

        // Act
        await sut.ProcessAsync(job, CancellationToken.None);

        // Assert: job is marked failed with the EC-002 message; AI is never called
        jobs.Received(1).SetFailed(job.Id, Arg.Is<string>(m => m.Contains("closed or abandoned")));
        await aiCore.DidNotReceiveWithAnyArgs().ReviewAsync(default!);
    }

    // T034 — AddOptionalReviewerAsync throws → PostAsync NOT called, job fails

    [Fact]
    public async Task ProcessAsync_ReviewerAddThrows_PostAsyncNotCalledAndJobFails()
    {
        var (jobs, prFetcher, aiCore, commentPoster, reviewerManager, clientRegistry, logger) = CreateDeps();

        var job = CreateJob();
        var pr = CreatePullRequest();

        SetupReviewerIdReturns(clientRegistry, job, Guid.NewGuid());
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
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

        var service = CreateService(jobs, prFetcher, aiCore, commentPoster, reviewerManager, clientRegistry, logger);

        await service.ProcessAsync(job, CancellationToken.None);

        jobs.Received(1).SetFailed(job.Id, Arg.Any<string>());
        await commentPoster.DidNotReceiveWithAnyArgs()
            .PostAsync(default!, default!, default!, default, default, default!);
    }

    [Fact]
    public async Task ProcessAsync_SuccessfulFlow_CallsCommentPosterWithCorrectParameters()
    {
        // Arrange
        var (jobs, prFetcher, aiCore, commentPoster, reviewerManager, clientRegistry, logger) = CreateDeps();

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
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<CancellationToken>())
            .Returns(result);

        var service = CreateService(jobs, prFetcher, aiCore, commentPoster, reviewerManager, clientRegistry, logger);

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
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_SuccessfulFlow_TransitionsJobToCompleted()
    {
        // Arrange
        var (jobs, prFetcher, aiCore, commentPoster, reviewerManager, clientRegistry, logger) = CreateDeps();

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
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<CancellationToken>())
            .Returns(result);

        var service = CreateService(jobs, prFetcher, aiCore, commentPoster, reviewerManager, clientRegistry, logger);

        // Act
        await service.ProcessAsync(job, CancellationToken.None);

        // Assert
        jobs.Received(1).SetResult(job.Id, result);
        await commentPoster.Received(1)
            .PostAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                result,
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>());
        jobs.DidNotReceive().SetFailed(Arg.Any<Guid>(), Arg.Any<string>());
    }

    // T026 — GetReviewerIdAsync returns non-null → AddOptionalReviewerAsync called with that GUID

    [Fact]
    public async Task ProcessAsync_WithConfiguredReviewerId_CallsAddOptionalReviewerWithThatGuid()
    {
        var (jobs, prFetcher, aiCore, commentPoster, reviewerManager, clientRegistry, logger) = CreateDeps();

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
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<CancellationToken>())
            .Returns(result);

        var service = CreateService(jobs, prFetcher, aiCore, commentPoster, reviewerManager, clientRegistry, logger);
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
}
