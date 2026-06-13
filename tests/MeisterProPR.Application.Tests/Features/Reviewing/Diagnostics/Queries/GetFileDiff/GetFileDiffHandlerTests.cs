// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license information.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Diagnostics.Queries.GetFileDiff;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MeisterProPR.Application.Tests.Features.Reviewing.Diagnostics.Queries.GetFileDiff;

public sealed class GetFileDiffHandlerTests
{
    [Fact]
    public async Task HandleAsync_WhenJobMissing_ReturnsNotFound()
    {
        var jobId = Guid.NewGuid();
        var jobRepository = Substitute.For<IJobRepository>();
        jobRepository.GetByIdWithFileResultsAsync(jobId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(null));

        var pullRequestFetcher = Substitute.For<IPullRequestFetcher>();
        var handler = new GetFileDiffHandler(jobRepository, pullRequestFetcher);

        var result = await handler.HandleAsync(new GetFileDiffQuery(jobId, Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(FileDiffAvailability.NotFound, result.Availability);
        Assert.Equal(string.Empty, result.FilePath);
        Assert.Equal(string.Empty, result.UnifiedDiff);
        Assert.False(result.IsBinary);
        Assert.Null(result.OriginalPath);
        Assert.NotNull(result.AvailabilityMessage);
    }

    [Fact]
    public async Task HandleAsync_WhenFileResultMissing_ReturnsNotFound()
    {
        var jobId = Guid.NewGuid();
        var job = CreateJob(jobId, []);

        var jobRepository = Substitute.For<IJobRepository>();
        jobRepository.GetByIdWithFileResultsAsync(jobId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(job));

        var pullRequestFetcher = Substitute.For<IPullRequestFetcher>();
        var handler = new GetFileDiffHandler(jobRepository, pullRequestFetcher);

        var result = await handler.HandleAsync(new GetFileDiffQuery(jobId, Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(FileDiffAvailability.NotFound, result.Availability);
        Assert.Equal(string.Empty, result.FilePath);
        Assert.NotNull(result.AvailabilityMessage);
    }

    [Fact]
    public async Task HandleAsync_WhenFileMatched_ReturnsAvailableDiff()
    {
        var jobId = Guid.NewGuid();
        var fileResult = CreateFileResult(jobId, "src/Services/ReviewService.cs");
        var job = CreateJob(jobId, [fileResult]);

        var unifiedDiff = "diff --git a/src/Services/ReviewService.cs b/src/Services/ReviewService.cs\n@@ -1,1 +1,2 @@\n+added line";

        var changedFile = new ChangedFile(
            "src/Services/ReviewService.cs",
            ChangeType.Edit,
            "full content",
            unifiedDiff,
            false,
            null);

        var jobRepository = Substitute.For<IJobRepository>();
        jobRepository.GetByIdWithFileResultsAsync(jobId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(job));

        var pullRequestFetcher = Substitute.For<IPullRequestFetcher>();
        pullRequestFetcher.FetchFileDiffAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.IterationId,
                fileResult.FilePath,
                Arg.Any<int?>(),
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChangedFile?>(changedFile));

        var handler = new GetFileDiffHandler(jobRepository, pullRequestFetcher);

        var result = await handler.HandleAsync(new GetFileDiffQuery(jobId, fileResult.Id), CancellationToken.None);

        Assert.Equal(FileDiffAvailability.Available, result.Availability);
        Assert.Equal("src/Services/ReviewService.cs", result.FilePath);
        Assert.Equal(unifiedDiff, result.UnifiedDiff);
        Assert.False(result.IsBinary);
        Assert.Equal("Modified", result.ChangeType);
        Assert.Null(result.AvailabilityMessage);
    }

    [Fact]
    public async Task HandleAsync_WhenFileIsBinary_ReturnsBinaryAvailability()
    {
        var jobId = Guid.NewGuid();
        var fileResult = CreateFileResult(jobId, "assets/logo.png");
        var job = CreateJob(jobId, [fileResult]);

        var changedFile = new ChangedFile(
            "assets/logo.png",
            ChangeType.Add,
            string.Empty,
            string.Empty,
            true,
            null);

        var jobRepository = Substitute.For<IJobRepository>();
        jobRepository.GetByIdWithFileResultsAsync(jobId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(job));

        var pullRequestFetcher = Substitute.For<IPullRequestFetcher>();
        pullRequestFetcher.FetchFileDiffAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.IterationId,
                fileResult.FilePath,
                Arg.Any<int?>(),
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChangedFile?>(changedFile));

        var handler = new GetFileDiffHandler(jobRepository, pullRequestFetcher);

        var result = await handler.HandleAsync(new GetFileDiffQuery(jobId, fileResult.Id), CancellationToken.None);

        Assert.Equal(FileDiffAvailability.Binary, result.Availability);
        Assert.True(result.IsBinary);
        Assert.Equal("Added", result.ChangeType);
        Assert.Equal(string.Empty, result.UnifiedDiff);
        Assert.NotNull(result.AvailabilityMessage);
    }

    [Fact]
    public async Task HandleAsync_WhenFileNotInPrChangedFiles_ReturnsNotFound()
    {
        var jobId = Guid.NewGuid();
        var fileResult = CreateFileResult(jobId, "src/NotInPr.cs");
        var job = CreateJob(jobId, [fileResult]);

        var jobRepository = Substitute.For<IJobRepository>();
        jobRepository.GetByIdWithFileResultsAsync(jobId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(job));

        var pullRequestFetcher = Substitute.For<IPullRequestFetcher>();
        pullRequestFetcher.FetchFileDiffAsync(
                job.OrganizationUrl,
                job.ProjectId,
                job.RepositoryId,
                job.PullRequestId,
                job.IterationId,
                fileResult.FilePath,
                Arg.Any<int?>(),
                job.ClientId,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ChangedFile?>(null));

        var handler = new GetFileDiffHandler(jobRepository, pullRequestFetcher);

        var result = await handler.HandleAsync(new GetFileDiffQuery(jobId, fileResult.Id), CancellationToken.None);

        Assert.Equal(FileDiffAvailability.NotFound, result.Availability);
        Assert.Equal("src/NotInPr.cs", result.FilePath);
        Assert.NotNull(result.AvailabilityMessage);
    }

    [Fact]
    public async Task HandleAsync_WhenProviderThrows_ReturnsProviderUnavailable()
    {
        var jobId = Guid.NewGuid();
        var fileResult = CreateFileResult(jobId, "src/Services/Foo.cs");
        var job = CreateJob(jobId, [fileResult]);

        var jobRepository = Substitute.For<IJobRepository>();
        jobRepository.GetByIdWithFileResultsAsync(jobId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(job));

        var pullRequestFetcher = Substitute.For<IPullRequestFetcher>();
        pullRequestFetcher.FetchFileDiffAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<string>(),
                Arg.Any<int?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("SCM unreachable"));

        var handler = new GetFileDiffHandler(jobRepository, pullRequestFetcher);

        var result = await handler.HandleAsync(new GetFileDiffQuery(jobId, fileResult.Id), CancellationToken.None);

        Assert.Equal(FileDiffAvailability.ProviderUnavailable, result.Availability);
        Assert.Equal("src/Services/Foo.cs", result.FilePath);
        Assert.NotNull(result.AvailabilityMessage);
    }

    private static ReviewFileResult CreateFileResult(Guid jobId, string filePath)
    {
        return new ReviewFileResult(jobId, filePath);
    }

    private static ReviewJob CreateJob(Guid jobId, IReadOnlyList<ReviewFileResult> fileResults)
    {
        var job = new ReviewJob(jobId, Guid.NewGuid(), "https://dev.azure.com/org", "project", "repo", 42, 1);
        foreach (var fileResult in fileResults)
        {
            job.FileReviewResults.Add(fileResult);
        }

        return job;
    }
}
