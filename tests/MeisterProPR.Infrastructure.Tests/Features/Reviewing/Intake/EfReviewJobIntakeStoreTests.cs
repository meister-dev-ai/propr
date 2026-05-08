// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Intake.Dtos;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Features.Reviewing.Intake.Persistence;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterProPR.Infrastructure.Tests.Features.Reviewing.Intake;

[Collection("PostgresIntegration")]
public sealed class EfReviewJobIntakeStoreTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private MeisterProPRDbContext _dbContext = null!;
    private EfReviewJobIntakeStore _store = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;

        this._dbContext = new MeisterProPRDbContext(options);
        await this._dbContext.ReviewJobs.ExecuteDeleteAsync();
        this._store = new EfReviewJobIntakeStore(this._dbContext);
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is not null)
        {
            await this._dbContext.DisposeAsync();
        }
    }

    [Fact]
    public async Task CreatePendingJobAsync_PersistsNewPendingJob()
    {
        var request = new SubmitReviewJobRequestDto("https://dev.azure.com/org", "proj", "repo", 10, 1);

        var job = await this._store.CreatePendingJobAsync(Guid.NewGuid(), request);

        Assert.Equal(JobStatus.Pending, job.Status);
        var persisted = await this._dbContext.ReviewJobs.FirstOrDefaultAsync(candidate => candidate.Id == job.Id);
        Assert.NotNull(persisted);
        Assert.Equal(request.PullRequestId, persisted!.PullRequestId);
    }

    [Fact]
    public async Task FindActiveJobAsync_CompletedJobIsNotReturned()
    {
        var request = new SubmitReviewJobRequestDto("https://dev.azure.com/org", "proj", "repo", 11, 1);
        var job = await this._store.CreatePendingJobAsync(Guid.NewGuid(), request);
        job.Status = JobStatus.Completed;
        job.CompletedAt = DateTimeOffset.UtcNow;
        await this._dbContext.SaveChangesAsync();

        var found = await this._store.FindActiveJobAsync(job.ClientId, request);

        Assert.Null(found);
    }

    [Fact]
    public async Task UpdatePrContextAsync_PersistsSnapshotAndNormalizesBranches()
    {
        var request = new SubmitReviewJobRequestDto("https://dev.azure.com/org", "proj", "repo", 12, 4);
        var job = await this._store.CreatePendingJobAsync(Guid.NewGuid(), request);

        await this._store.UpdatePrContextAsync(
            job.Id,
            "Add feature",
            "repo-display",
            "refs/heads/feature/x",
            "refs/heads/main");

        var refreshed = await this._store.GetByIdAsync(job.Id);
        Assert.NotNull(refreshed);
        Assert.Equal("Add feature", refreshed!.PrTitle);
        Assert.Equal("repo-display", refreshed.PrRepositoryName);
        Assert.Equal("feature/x", refreshed.PrSourceBranch);
        Assert.Equal("main", refreshed.PrTargetBranch);
    }

    [Fact]
    public async Task FindActiveJobAsync_ProviderNeutralReviewRevisionMatchesNormalizedTarget()
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.example.com");
        var repository = new RepositoryRef(host, "repo-gh-1", "acme", "acme/propr");
        var request = new SubmitReviewJobRequestDto(
            host.HostBaseUrl,
            repository.OwnerOrNamespace,
            repository.ExternalRepositoryId,
            42,
            1)
        {
            Provider = ScmProvider.GitHub,
            Host = host,
            Repository = repository,
            CodeReview = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42),
            ReviewRevision = new ReviewRevision("head-sha", "base-sha", "start-sha", "revision-1", "patch-1"),
        };

        var job = await this._store.CreatePendingJobAsync(Guid.NewGuid(), request);

        var found = await this._store.FindActiveJobAsync(job.ClientId, request);

        Assert.NotNull(found);
        Assert.Equal(job.Id, found!.Id);
        Assert.Equal(ScmProvider.GitHub, found.Provider);
        Assert.Equal("patch-1", found.ReviewPatchIdentity);
    }

    [Fact]
    public async Task FindActiveJobAsync_ProviderNeutralReviewRevisionMatchesAlternateRepositoryIdShape()
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.example.com");
        var storedRepository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var incomingRepository = new RepositoryRef(host, "acme/propr", "acme", "acme/propr");
        var storedRequest = new SubmitReviewJobRequestDto(
            host.HostBaseUrl,
            storedRepository.OwnerOrNamespace,
            storedRepository.ExternalRepositoryId,
            42,
            1)
        {
            Provider = ScmProvider.GitHub,
            Host = host,
            Repository = storedRepository,
            CodeReview = new CodeReviewRef(storedRepository, CodeReviewPlatformKind.PullRequest, "42", 42),
            ReviewRevision = new ReviewRevision("head-sha", "base-sha", "start-sha", "revision-1", "patch-1"),
        };

        var incomingRequest = storedRequest with
        {
            RepositoryId = incomingRepository.ExternalRepositoryId,
            Repository = incomingRepository,
            CodeReview = new CodeReviewRef(incomingRepository, CodeReviewPlatformKind.PullRequest, "42", 42),
        };

        var job = await this._store.CreatePendingJobAsync(Guid.NewGuid(), storedRequest);

        var found = await this._store.FindActiveJobAsync(job.ClientId, incomingRequest);

        Assert.NotNull(found);
        Assert.Equal(job.Id, found!.Id);
    }

    [Fact]
    public async Task CreatePendingJobAsync_RequestedReviewerIdentity_DoesNotRedefinePersistedPostingContext()
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.example.com");
        var repository = new RepositoryRef(host, "repo-gh-1", "acme", "acme/propr");
        var request = new SubmitReviewJobRequestDto(
            host.HostBaseUrl,
            repository.OwnerOrNamespace,
            repository.ExternalRepositoryId,
            42,
            1)
        {
            Provider = ScmProvider.GitHub,
            Host = host,
            Repository = repository,
            CodeReview = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42),
            ReviewRevision = new ReviewRevision("head-sha", "base-sha", "start-sha", "revision-1", "patch-1"),
            RequestedReviewerIdentity = new ReviewerIdentity(
                host,
                "user-1",
                "configured-reviewer",
                "Configured Reviewer",
                false),
        };

        var job = await this._store.CreatePendingJobAsync(Guid.NewGuid(), request);
        var persisted = await this._dbContext.ReviewJobs.FirstOrDefaultAsync(candidate => candidate.Id == job.Id);

        Assert.NotNull(persisted);
        Assert.Equal(ScmProvider.GitHub, persisted!.Provider);
        Assert.Equal("https://github.example.com", persisted.HostBaseUrl);
        Assert.Equal("acme", persisted.RepositoryOwnerOrNamespace);
        Assert.Equal("acme/propr", persisted.RepositoryProjectPath);
        Assert.Equal("42", persisted.ExternalCodeReviewId);
        Assert.Null(persisted.PrTitle);
        Assert.Null(persisted.PrRepositoryName);
        Assert.Null(persisted.PrSourceBranch);
        Assert.Null(persisted.PrTargetBranch);
    }
}
