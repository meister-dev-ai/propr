using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Interfaces;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterProPR.Application.Tests.Services;

public class ReviewOrchestrationServiceTests
{
    private static ReviewJob CreateJob()
    {
        return new ReviewJob(Guid.NewGuid(), "test-client", "https://dev.azure.com/org", "proj", "repo", 1, 1);
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

    [Fact]
    public async Task ProcessAsync_AiException_TransitionsJobToFailed()
    {
        // Arrange
        var jobs = Substitute.For<IJobRepository>();
        var prFetcher = Substitute.For<IPullRequestFetcher>();
        var aiCore = Substitute.For<IAiReviewCore>();
        var commentPoster = Substitute.For<IAdoCommentPoster>();
        var logger = Substitute.For<ILogger<ReviewOrchestrationService>>();

        var job = CreateJob();
        var pr = CreatePullRequest();

        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<CancellationToken>())
            .Throws(new Exception("AI error"));

        var service = new ReviewOrchestrationService(jobs, prFetcher, aiCore, commentPoster, logger);

        // Act
        await service.ProcessAsync(job, CancellationToken.None);

        // Assert
        jobs.Received(1).SetFailed(job.Id, Arg.Is<string>(s => s.Contains("AI error")));
        jobs.DidNotReceive().SetResult(Arg.Any<Guid>(), Arg.Any<ReviewResult>());
    }

    [Fact]
    public async Task ProcessAsync_CommentPostException_TransitionsJobToFailed()
    {
        // Arrange
        var jobs = Substitute.For<IJobRepository>();
        var prFetcher = Substitute.For<IPullRequestFetcher>();
        var aiCore = Substitute.For<IAiReviewCore>();
        var commentPoster = Substitute.For<IAdoCommentPoster>();
        var logger = Substitute.For<ILogger<ReviewOrchestrationService>>();

        var job = CreateJob();
        var pr = CreatePullRequest();
        var result = CreateReviewResult();

        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
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
                Arg.Any<CancellationToken>())
            .Throws(new Exception("Comment post error"));

        var service = new ReviewOrchestrationService(jobs, prFetcher, aiCore, commentPoster, logger);

        // Act
        await service.ProcessAsync(job, CancellationToken.None);

        // Assert
        jobs.Received(1).SetFailed(job.Id, Arg.Is<string>(s => s.Contains("Comment post error")));
    }

    [Fact]
    public async Task ProcessAsync_FetchException_TransitionsJobToFailed()
    {
        // Arrange
        var jobs = Substitute.For<IJobRepository>();
        var prFetcher = Substitute.For<IPullRequestFetcher>();
        var aiCore = Substitute.For<IAiReviewCore>();
        var commentPoster = Substitute.For<IAdoCommentPoster>();
        var logger = Substitute.For<ILogger<ReviewOrchestrationService>>();

        var job = CreateJob();
        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Throws(new Exception("ADO fetch error"));

        var service = new ReviewOrchestrationService(jobs, prFetcher, aiCore, commentPoster, logger);

        // Act
        await service.ProcessAsync(job, CancellationToken.None);

        // Assert
        jobs.Received(1).SetFailed(job.Id, Arg.Is<string>(s => s.Contains("ADO fetch error")));
        jobs.DidNotReceive().SetResult(Arg.Any<Guid>(), Arg.Any<ReviewResult>());
    }

    [Fact]
    public async Task ProcessAsync_SuccessfulFlow_CallsCommentPosterWithCorrectParameters()
    {
        // Arrange
        var jobs = Substitute.For<IJobRepository>();
        var prFetcher = Substitute.For<IPullRequestFetcher>();
        var aiCore = Substitute.For<IAiReviewCore>();
        var commentPoster = Substitute.For<IAdoCommentPoster>();
        var logger = Substitute.For<ILogger<ReviewOrchestrationService>>();

        var job = CreateJob();
        var pr = CreatePullRequest();
        var result = CreateReviewResult();

        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<CancellationToken>())
            .Returns(result);

        var service = new ReviewOrchestrationService(jobs, prFetcher, aiCore, commentPoster, logger);

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
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessAsync_SuccessfulFlow_TransitionsJobToCompleted()
    {
        // Arrange
        var jobs = Substitute.For<IJobRepository>();
        var prFetcher = Substitute.For<IPullRequestFetcher>();
        var aiCore = Substitute.For<IAiReviewCore>();
        var commentPoster = Substitute.For<IAdoCommentPoster>();
        var logger = Substitute.For<ILogger<ReviewOrchestrationService>>();

        var job = CreateJob();
        var pr = CreatePullRequest();
        var result = CreateReviewResult();

        prFetcher.FetchAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(pr);
        aiCore.ReviewAsync(Arg.Any<PullRequest>(), Arg.Any<CancellationToken>())
            .Returns(result);

        var service = new ReviewOrchestrationService(jobs, prFetcher, aiCore, commentPoster, logger);

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
                Arg.Any<CancellationToken>());
        jobs.DidNotReceive().SetFailed(Arg.Any<Guid>(), Arg.Any<string>());
    }
}