// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license information.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.ReviewArchive;
using MeisterProPR.Application.Features.Reviewing.Diagnostics.Queries.GetFileDiff;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Support;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Features.ReviewArchive.Persistence;
using MeisterProPR.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterProPR.Application.Tests.Features.Reviewing.Diagnostics.Queries.GetFileDiff;

/// <summary>
///     Component-integration coverage for <see cref="GetFileDiffHandler" /> wired to the real
///     <see cref="ReviewArchiveStore" /> over a real (in-memory) EF context and a real
///     <see cref="ISecretProtectionCodec" />. Only the SCM diff fetcher and the connection lookup are
///     faked. This verifies the serve-from-storage decision and the remote fallback against the actual
///     persistence component, not a mocked store.
/// </summary>
public sealed class GetFileDiffHandlerStorageIntegrationTests : IDisposable
{
    private const string OrganizationUrl = "https://dev.azure.com/org";
    private const string RepositoryId = "repo";
    private const int PullRequestId = 42;

    private readonly MeisterProPRDbContext _dbContext;
    private readonly ReviewArchiveStore _store;

    public GetFileDiffHandlerStorageIntegrationTests()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"GetFileDiffHandlerStorageIntegrationTests-{Guid.NewGuid():N}")
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);
        this._store = new ReviewArchiveStore(this._dbContext, CreateCodec());
    }

    public void Dispose()
    {
        this._dbContext.Dispose();
    }

    [Fact]
    public async Task HandleAsync_WhenRetentionOnAndDiffStored_ServesFromRealStore_WithoutFetchingRemote()
    {
        var jobId = Guid.NewGuid();
        var fileResult = new ReviewFileResult(jobId, "src/Services/ReviewService.cs");
        var job = CreateJob(jobId, fileResult);

        var connectionId = Guid.NewGuid();
        const string storedDiff = "@@ -1,1 +1,2 @@\n+stored line\n+stored line two";

        // Seed the diff into the real store under the same revision key the ingestion path uses for this
        // job, so the handler's revision-scoped lookup resolves it.
        await this._store.SaveFileDiffsAsync(
            new PullRequestRetentionKey(job.ClientId, connectionId, job.RepositoryId, job.PullRequestId),
            ReviewRevisionKeys.GetStoredKey(job.ReviewRevisionReference, job.IterationId),
            [new RetainedFileDiffSnapshot("src/Services/ReviewService.cs", "Modified", false, storedDiff)]);

        var jobRepository = Substitute.For<IJobRepository>();
        jobRepository.GetByIdWithFileResultsAsync(jobId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(job));

        var pullRequestFetcher = Substitute.For<IPullRequestFetcher>();

        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetByClientIdAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ClientScmConnectionDto>>([CreateConnection(connectionId, job.ClientId, true)]));

        var handler = new GetFileDiffHandler(jobRepository, pullRequestFetcher, this._store, connectionRepository);

        var result = await handler.HandleAsync(new GetFileDiffQuery(jobId, fileResult.Id), CancellationToken.None);

        Assert.Equal(FileDiffAvailability.Available, result.Availability);
        Assert.Equal("src/Services/ReviewService.cs", result.FilePath);
        Assert.Equal(storedDiff, result.UnifiedDiff);
        Assert.Equal("Modified", result.ChangeType);
        Assert.False(result.IsBinary);

        // Served from storage: the source control provider must never be contacted.
        await pullRequestFetcher.DidNotReceiveWithAnyArgs()
            .FetchFileDiffAsync(default!, default!, default!, default, default, default!);
    }

    [Fact]
    public async Task HandleAsync_WhenRetentionOff_FallsBackToRemote_EvenWhenDiffStored()
    {
        var jobId = Guid.NewGuid();
        var fileResult = new ReviewFileResult(jobId, "src/Services/ReviewService.cs");
        var job = CreateJob(jobId, fileResult);

        var connectionId = Guid.NewGuid();

        // A stored diff exists, but the owning connection has diff retention disabled.
        await this._store.SaveFileDiffsAsync(
            new PullRequestRetentionKey(job.ClientId, connectionId, job.RepositoryId, job.PullRequestId),
            "rev-1",
            [new RetainedFileDiffSnapshot("src/Services/ReviewService.cs", "Modified", false, "@@ stored @@")]);

        const string remoteDiff = "@@ -1,1 +1,2 @@\n+remote line";
        var changedFile = new ChangedFile(
            "src/Services/ReviewService.cs",
            ChangeType.Edit,
            "full content",
            remoteDiff);

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

        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetByClientIdAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ClientScmConnectionDto>>([CreateConnection(connectionId, job.ClientId, false)]));

        var handler = new GetFileDiffHandler(jobRepository, pullRequestFetcher, this._store, connectionRepository);

        var result = await handler.HandleAsync(new GetFileDiffQuery(jobId, fileResult.Id), CancellationToken.None);

        Assert.Equal(FileDiffAvailability.Available, result.Availability);
        Assert.Equal(remoteDiff, result.UnifiedDiff);

        // Retention is off, so the handler ignores the store and reaches the remote fetcher exactly once.
        await pullRequestFetcher.Received(1).FetchFileDiffAsync(
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.PullRequestId,
            job.IterationId,
            fileResult.FilePath,
            Arg.Any<int?>(),
            job.ClientId,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_WhenRetentionOnButNoStoredDiffForFile_FallsBackToRemote()
    {
        var jobId = Guid.NewGuid();
        var fileResult = new ReviewFileResult(jobId, "src/Services/ReviewService.cs");
        var job = CreateJob(jobId, fileResult);

        var connectionId = Guid.NewGuid();

        // The store has a diff for a *different* file, so this file falls through to the remote fetch.
        await this._store.SaveFileDiffsAsync(
            new PullRequestRetentionKey(job.ClientId, connectionId, job.RepositoryId, job.PullRequestId),
            "rev-1",
            [new RetainedFileDiffSnapshot("src/Other.cs", "Modified", false, "@@ other @@")]);

        const string remoteDiff = "@@ -1,1 +1,2 @@\n+remote line";
        var changedFile = new ChangedFile(
            "src/Services/ReviewService.cs",
            ChangeType.Edit,
            "full content",
            remoteDiff);

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

        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetByClientIdAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ClientScmConnectionDto>>([CreateConnection(connectionId, job.ClientId, true)]));

        var handler = new GetFileDiffHandler(jobRepository, pullRequestFetcher, this._store, connectionRepository);

        var result = await handler.HandleAsync(new GetFileDiffQuery(jobId, fileResult.Id), CancellationToken.None);

        Assert.Equal(FileDiffAvailability.Available, result.Availability);
        Assert.Equal(remoteDiff, result.UnifiedDiff);
        await pullRequestFetcher.Received(1).FetchFileDiffAsync(
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.PullRequestId,
            job.IterationId,
            fileResult.FilePath,
            Arg.Any<int?>(),
            job.ClientId,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_MultiIteration_ServesStoredDiffScopedToJobsRevisionKey_NotNewest()
    {
        // The job under test reviewed iteration 5. Two iterations of the same file are retained: the job's
        // own iteration (5) and a later iteration (9). The stored lookup must be scoped to the job's
        // revision key so it serves iteration 5's diff, not the newest retained one.
        const int jobIteration = 5;
        const int newerIteration = 9;

        var jobId = Guid.NewGuid();
        var fileResult = new ReviewFileResult(jobId, "src/Services/ReviewService.cs");
        var job = CreateJob(jobId, fileResult, jobIteration);

        var connectionId = Guid.NewGuid();
        const string iterationFiveDiff = "@@ -1,1 +1,2 @@\n+iteration five line";
        const string iterationNineDiff = "@@ -1,1 +1,3 @@\n+iteration nine line";

        // Diff ingestion stores each increment under ReviewRevisionKeys.GetStoredKey(revision, iterationId).
        // For a job without a revision reference that key is the iteration id as a string.
        await this._store.SaveFileDiffsAsync(
            new PullRequestRetentionKey(job.ClientId, connectionId, job.RepositoryId, job.PullRequestId),
            jobIteration.ToString(),
            [new RetainedFileDiffSnapshot("src/Services/ReviewService.cs", "Modified", false, iterationFiveDiff)]);

        // The newer iteration is stored afterwards, so it is the most-recently-created (newest) row.
        await this._store.SaveFileDiffsAsync(
            new PullRequestRetentionKey(job.ClientId, connectionId, job.RepositoryId, job.PullRequestId),
            newerIteration.ToString(),
            [new RetainedFileDiffSnapshot("src/Services/ReviewService.cs", "Modified", false, iterationNineDiff)]);

        var jobRepository = Substitute.For<IJobRepository>();
        jobRepository.GetByIdWithFileResultsAsync(jobId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReviewJob?>(job));

        var pullRequestFetcher = Substitute.For<IPullRequestFetcher>();

        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetByClientIdAsync(job.ClientId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ClientScmConnectionDto>>([CreateConnection(connectionId, job.ClientId, true)]));

        var handler = new GetFileDiffHandler(jobRepository, pullRequestFetcher, this._store, connectionRepository);

        var result = await handler.HandleAsync(new GetFileDiffQuery(jobId, fileResult.Id), CancellationToken.None);

        Assert.Equal(FileDiffAvailability.Available, result.Availability);
        // The job's own iteration is served, not the newest retained iteration.
        Assert.Equal(iterationFiveDiff, result.UnifiedDiff);
        Assert.NotEqual(iterationNineDiff, result.UnifiedDiff);

        // Served from storage, scoped to the job's revision: the provider is never contacted.
        await pullRequestFetcher.DidNotReceiveWithAnyArgs()
            .FetchFileDiffAsync(default!, default!, default!, default, default, default!);
    }

    private static ReviewJob CreateJob(Guid jobId, ReviewFileResult fileResult, int iterationId = 1)
    {
        var job = new ReviewJob(jobId, Guid.NewGuid(), OrganizationUrl, "project", RepositoryId, PullRequestId, iterationId);
        job.FileReviewResults.Add(fileResult);
        return job;
    }

    private static ClientScmConnectionDto CreateConnection(Guid connectionId, Guid clientId, bool storeDiffs)
    {
        var now = DateTimeOffset.UtcNow;
        return new ClientScmConnectionDto(
            connectionId,
            clientId,
            ScmProvider.AzureDevOps,
            OrganizationUrl,
            ScmAuthenticationKind.PersonalAccessToken,
            "Azure DevOps",
            true,
            "verified",
            now,
            null,
            null,
            now,
            now)
        {
            StoreDiffs = storeDiffs,
        };
    }

    private static ISecretProtectionCodec CreateCodec()
    {
        var keysDirectory = Path.Combine(
            Path.GetTempPath(),
            $"MeisterProPR.GetFileDiffHandlerStorageIntegrationTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysDirectory);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("MeisterProPR.Tests")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

        var provider = services.BuildServiceProvider();
        return new SecretProtectionCodec(provider.GetRequiredService<IDataProtectionProvider>());
    }
}
