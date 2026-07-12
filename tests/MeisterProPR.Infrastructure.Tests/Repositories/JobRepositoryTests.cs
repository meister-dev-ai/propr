// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Support;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Integration tests for <see cref="JobRepository" /> against a real PostgreSQL instance.
///     Uses a shared <see cref="PostgresContainerFixture" /> (one container for the whole collection)
///     to avoid the Podman port-binding instability of starting a container per test method.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class JobRepositoryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private MeisterProPRDbContext _dbContext = null!;
    private JobRepository _repo = null!;

    public async Task DisposeAsync()
    {
        if (this._dbContext is not null)
        {
            await this._dbContext.DisposeAsync();
        }
    }

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);
        // Wipe job rows between tests so count-based assertions stay deterministic.
        await this._dbContext.ReviewJobs.ExecuteDeleteAsync();
        var contextFactory = new TestDbContextFactory(options);
        this._repo = new JobRepository(this._dbContext, contextFactory, NullLogger<JobRepository>.Instance);
    }

    private static ReviewJob MakeJob(
        Guid? clientId = null,
        string orgUrl = "https://dev.azure.com/org",
        string projectId = "proj",
        string repoId = "repo",
        int prId = 1,
        int iterationId = 1)
    {
        return new ReviewJob(Guid.NewGuid(), clientId ?? Guid.NewGuid(), orgUrl, projectId, repoId, prId, iterationId);
    }

    private static ReviewFileResult CreateCompletedFileResult(Guid jobId, string path)
    {
        var result = new ReviewFileResult(jobId, path);
        result.MarkCompleted($"summary for {path}", []);
        return result;
    }

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value)
    {
        const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;
        return new DateTimeOffset(value.Ticks - value.Ticks % TicksPerMicrosecond, value.Offset);
    }

    private static ReviewJobProtocol MakeProtocol(
        Guid jobId,
        DateTimeOffset? completedAt = null,
        string? outcome = null)
    {
        return new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            AttemptNumber = 1,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = completedAt,
            Outcome = outcome,
        };
    }


    [Fact]
    public async Task Add_ThenGetById_ReturnsJob()
    {
        var job = MakeJob();
        await this._repo.AddAsync(job);

        var fetched = this._repo.GetById(job.Id);
        Assert.NotNull(fetched);
        Assert.Equal(job.Id, fetched.Id);
        Assert.Equal(JobStatus.Pending, fetched.Status);
    }

    [Fact]
    public async Task Add_WithSelectedProCursorSourceScope_PersistsAndHydratesSnapshot()
    {
        var sourceA = Guid.NewGuid();
        var sourceB = Guid.NewGuid();
        var job = MakeJob();
        this._dbContext.Clients.Add(
            new ClientRecord
            {
                Id = job.ClientId,
                TenantId = TenantCatalog.SystemTenantId,
                DisplayName = "Test Client",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        this._dbContext.ProCursorKnowledgeSources.AddRange(
            new ProCursorKnowledgeSource(
                sourceA,
                job.ClientId,
                "Source A",
                ProCursorSourceKind.Repository,
                job.OrganizationUrl,
                job.ProjectId,
                "repo-a",
                "main",
                null,
                true,
                "auto"),
            new ProCursorKnowledgeSource(
                sourceB,
                job.ClientId,
                "Source B",
                ProCursorSourceKind.Repository,
                job.OrganizationUrl,
                job.ProjectId,
                "repo-b",
                "main",
                null,
                true,
                "auto"));
        await this._dbContext.SaveChangesAsync();

        job.SetProCursorSourceScope(ProCursorSourceScopeMode.SelectedSources, [sourceA, sourceB, sourceA]);

        await this._repo.AddAsync(job);

        var fetched = this._repo.GetById(job.Id);

        Assert.NotNull(fetched);
        Assert.Equal(ProCursorSourceScopeMode.SelectedSources, fetched!.ProCursorSourceScopeMode);
        Assert.Equal([sourceA, sourceB], fetched.ProCursorSourceIds);
    }

    [Fact]
    public async Task FindActiveJob_ReturnsNullForCompletedJob()
    {
        // Completed jobs are terminal and must not be treated as active work.
        // Crawl dedup against same-iteration completed jobs is handled separately.
        var job = MakeJob();
        await this._repo.AddAsync(job);
        await this._repo.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing);
        await this._repo.SetResultAsync(job.Id, new ReviewResult("summary", []));

        var found = this._repo.FindActiveJob(
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.PullRequestId,
            job.IterationId);
        Assert.Null(found);
    }

    [Fact]
    public async Task FindCompletedJob_ReturnsMostRecentCompletedJob()
    {
        var job1 = MakeJob(prId: 500, iterationId: 2);
        await this._repo.AddAsync(job1);
        await this._repo.TryTransitionAsync(job1.Id, JobStatus.Pending, JobStatus.Processing);
        await this._repo.SetResultAsync(job1.Id, new ReviewResult("summary 1", []));

        await Task.Delay(10);

        var job2 = MakeJob(
            job1.ClientId,
            job1.OrganizationUrl,
            job1.ProjectId,
            job1.RepositoryId,
            job1.PullRequestId,
            job1.IterationId);
        await this._repo.AddAsync(job2);
        await this._repo.TryTransitionAsync(job2.Id, JobStatus.Pending, JobStatus.Processing);
        await this._repo.SetResultAsync(job2.Id, new ReviewResult("summary 2", []));

        var found = this._repo.FindCompletedJob(
            job1.OrganizationUrl,
            job1.ProjectId,
            job1.RepositoryId,
            job1.PullRequestId,
            job1.IterationId);

        Assert.NotNull(found);
        Assert.Equal(job2.Id, found.Id);
    }

    [Fact]
    public async Task FindFailedJob_ReturnsFailedJob_AndIgnoresCompletedOrActiveJobs()
    {
        // Active job for a different iteration must be ignored.
        var activeJob = MakeJob(prId: 510, iterationId: 1);
        await this._repo.AddAsync(activeJob);

        // Completed job for the target iteration must not be returned by FindFailedJob.
        var completedJob = MakeJob(activeJob.ClientId, activeJob.OrganizationUrl, activeJob.ProjectId, activeJob.RepositoryId, 510, 3);
        await this._repo.AddAsync(completedJob);
        await this._repo.TryTransitionAsync(completedJob.Id, JobStatus.Pending, JobStatus.Processing);
        await this._repo.SetResultAsync(completedJob.Id, new ReviewResult("done", []));

        // Failed job for the target iteration is the expected match.
        var failedJob = MakeJob(activeJob.ClientId, activeJob.OrganizationUrl, activeJob.ProjectId, activeJob.RepositoryId, 510, 3);
        await this._repo.AddAsync(failedJob);
        await this._repo.SetFailedAsync(failedJob.Id, "boom");

        var found = this._repo.FindFailedJob(
            activeJob.OrganizationUrl,
            activeJob.ProjectId,
            activeJob.RepositoryId,
            510,
            3);

        Assert.NotNull(found);
        Assert.Equal(failedJob.Id, found.Id);

        // No failed job for a different iteration.
        Assert.Null(this._repo.FindFailedJob(activeJob.OrganizationUrl, activeJob.ProjectId, activeJob.RepositoryId, 510, 99));
    }

    [Fact]
    public async Task GetCompletedJobWithFileResultsByStoredRevisionAsync_ReturnsMatchingCompletedJob()
    {
        var job = MakeJob(prId: 700, iterationId: 5);
        job.SetReviewRevision(new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha"));
        await this._repo.AddAsync(job);
        await this._repo.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing);
        await this._repo.SetResultAsync(job.Id, new ReviewResult("summary", []));

        var storedRevisionKey = ReviewRevisionKeys.GetStoredKey(job.ReviewRevisionReference, job.IterationId);

        var found = await this._repo.GetCompletedJobWithFileResultsByStoredRevisionAsync(
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.PullRequestId,
            storedRevisionKey);

        Assert.NotNull(found);
        Assert.Equal(job.Id, found!.Id);
    }

    [Fact]
    public async Task GetCompletedJobWithFileResultsByStoredRevisionAsync_IgnoresFailedAndCancelledJobs()
    {
        var failedJob = MakeJob(prId: 701, iterationId: 5);
        failedJob.SetReviewRevision(new ReviewRevision("failed-head", "base-sha", null, "failed-head", "base-sha...failed-head"));
        await this._repo.AddAsync(failedJob);
        await this._repo.SetFailedAsync(failedJob.Id, "boom");

        var cancelledJob = MakeJob(failedJob.ClientId, failedJob.OrganizationUrl, failedJob.ProjectId, failedJob.RepositoryId, failedJob.PullRequestId, 6);
        cancelledJob.SetReviewRevision(new ReviewRevision("cancelled-head", "base-sha", null, "cancelled-head", "base-sha...cancelled-head"));
        await this._repo.AddAsync(cancelledJob);
        await this._repo.SetCancelledAsync(cancelledJob.Id);

        var found = await this._repo.GetCompletedJobWithFileResultsByStoredRevisionAsync(
            failedJob.OrganizationUrl,
            failedJob.ProjectId,
            failedJob.RepositoryId,
            failedJob.PullRequestId,
            "base-sha...cancelled-head");

        Assert.Null(found);
    }

    [Fact]
    public async Task GetBestTerminalJobWithFileResultsByStoredRevisionAsync_PrefersJobWithMoreReusableCompletedFiles()
    {
        var olderFailedJob = MakeJob(prId: 702, iterationId: 5);
        olderFailedJob.SetReviewRevision(new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha"));
        await this._repo.AddAsync(olderFailedJob);
        await this._repo.SetFailedAsync(olderFailedJob.Id, "older boom");

        await this._repo.AddFileResultAsync(CreateCompletedFileResult(olderFailedJob.Id, "src/A.cs"));
        await this._repo.AddFileResultAsync(CreateCompletedFileResult(olderFailedJob.Id, "src/B.cs"));

        await Task.Delay(10);

        var newerCancelledJob = MakeJob(
            olderFailedJob.ClientId, olderFailedJob.OrganizationUrl, olderFailedJob.ProjectId, olderFailedJob.RepositoryId, olderFailedJob.PullRequestId, 6);
        newerCancelledJob.SetReviewRevision(new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha"));
        await this._repo.AddAsync(newerCancelledJob);
        await this._repo.SetCancelledAsync(newerCancelledJob.Id);

        await this._repo.AddFileResultAsync(CreateCompletedFileResult(newerCancelledJob.Id, "src/A.cs"));

        var storedRevisionKey = ReviewRevisionKeys.GetStoredKey(olderFailedJob.ReviewRevisionReference, olderFailedJob.IterationId);

        var found = await this._repo.GetBestTerminalJobWithFileResultsByStoredRevisionAsync(
            olderFailedJob.OrganizationUrl,
            olderFailedJob.ProjectId,
            olderFailedJob.RepositoryId,
            olderFailedJob.PullRequestId,
            storedRevisionKey);

        Assert.NotNull(found);
        Assert.Equal(olderFailedJob.Id, found!.Id);
    }

    [Fact]
    public async Task FindActiveJob_ReturnsNullForFailedJob()
    {
        // T039 / T009: Failed job should return null to allow retry
        var job = MakeJob();
        await this._repo.AddAsync(job);
        await this._repo.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing);
        await this._repo.SetFailedAsync(job.Id, "test error");

        var found = this._repo.FindActiveJob(
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.PullRequestId,
            job.IterationId);
        Assert.Null(found);
    }


    [Fact]
    public async Task FindActiveJob_ReturnsPendingJob()
    {
        var job = MakeJob();
        await this._repo.AddAsync(job);

        var found = this._repo.FindActiveJob(
            job.OrganizationUrl,
            job.ProjectId,
            job.RepositoryId,
            job.PullRequestId,
            job.IterationId);
        Assert.NotNull(found);
        Assert.Equal(job.Id, found.Id);
    }

    [Fact]
    public async Task TryAddIfNoActiveDuplicateAsync_WithMatchingRevision_ReturnsExistingDuplicate()
    {
        var job = MakeJob(prId: 42, iterationId: 11);
        job.SetProviderReviewContext(
            new CodeReviewRef(
                new RepositoryRef(new ProviderHostRef(ScmProvider.GitHub, "https://github.com"), "repo", "acme", "acme/repo"),
                CodeReviewPlatformKind.PullRequest,
                "42",
                42));
        job.SetReviewRevision(new ReviewRevision("head-sha", "base-sha", "start-sha", "revision-1", "patch-1"));
        await this._repo.AddAsync(job);

        var duplicate = MakeJob(job.ClientId, job.OrganizationUrl, job.ProjectId, job.RepositoryId, job.PullRequestId, 99);
        duplicate.SetProviderReviewContext(job.CodeReviewReference);
        duplicate.SetReviewRevision(new ReviewRevision("head-sha", "base-sha", "start-sha", "revision-1", "patch-1"));

        var result = await this._repo.TryAddIfNoActiveDuplicateAsync(duplicate);

        Assert.False(result.WasAdded);
        Assert.NotNull(result.DuplicateJob);
        Assert.Equal(job.Id, result.DuplicateJob!.Id);
        Assert.Equal(0, result.CancelledSupersededJobCount);
        Assert.Equal(1, await this._dbContext.ReviewJobs.CountAsync());
    }

    [Fact]
    public async Task TryAddIfNoActiveDuplicateAsync_WithMatchingRevisionAndAlternateRepositoryIdShape_ReturnsExistingDuplicate()
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var storedRepository = new RepositoryRef(host, "101", "acme", "acme/repo");
        var incomingRepository = new RepositoryRef(host, "acme/repo", "acme", "acme/repo");

        var job = MakeJob(prId: 42, iterationId: 11, repoId: storedRepository.ExternalRepositoryId, projectId: "acme");
        job.SetProviderReviewContext(new CodeReviewRef(storedRepository, CodeReviewPlatformKind.PullRequest, "42", 42));
        job.SetReviewRevision(new ReviewRevision("head-sha", "base-sha", "start-sha", "revision-1", "patch-1"));
        await this._repo.AddAsync(job);

        var duplicate = MakeJob(job.ClientId, job.OrganizationUrl, job.ProjectId, incomingRepository.ExternalRepositoryId, job.PullRequestId, 99);
        duplicate.SetProviderReviewContext(new CodeReviewRef(incomingRepository, CodeReviewPlatformKind.PullRequest, "42", 42));
        duplicate.SetReviewRevision(new ReviewRevision("head-sha", "base-sha", "start-sha", "revision-1", "patch-1"));

        var result = await this._repo.TryAddIfNoActiveDuplicateAsync(duplicate);

        Assert.False(result.WasAdded);
        Assert.NotNull(result.DuplicateJob);
        Assert.Equal(job.Id, result.DuplicateJob!.Id);
        Assert.Equal(1, await this._dbContext.ReviewJobs.CountAsync());
    }

    [Fact]
    public async Task TryAddIfNoActiveDuplicateAsync_WithSupersededRevision_CancelsOlderActiveJobAndAddsNewJob()
    {
        var older = MakeJob(prId: 42, iterationId: 10);
        older.SetProviderReviewContext(
            new CodeReviewRef(
                new RepositoryRef(new ProviderHostRef(ScmProvider.GitHub, "https://github.com"), "repo", "acme", "acme/repo"),
                CodeReviewPlatformKind.PullRequest,
                "42",
                42));
        older.SetReviewRevision(new ReviewRevision("old-head", "base-sha", "start-sha", "revision-0", "patch-0"));
        older.Status = JobStatus.Processing;
        await this._repo.AddAsync(older);

        var newer = MakeJob(older.ClientId, older.OrganizationUrl, older.ProjectId, older.RepositoryId, older.PullRequestId, 11);
        newer.SetProviderReviewContext(older.CodeReviewReference);
        newer.SetReviewRevision(new ReviewRevision("new-head", "base-sha", "start-sha", "revision-1", "patch-1"));

        var result = await this._repo.TryAddIfNoActiveDuplicateAsync(newer);

        Assert.True(result.WasAdded);
        Assert.Null(result.DuplicateJob);
        Assert.Equal(1, result.CancelledSupersededJobCount);

        var persistedOlder = await this._dbContext.ReviewJobs.FirstAsync(candidate => candidate.Id == older.Id);
        var persistedNewer = await this._dbContext.ReviewJobs.FirstAsync(candidate => candidate.Id == newer.Id);
        Assert.Equal(JobStatus.Cancelled, persistedOlder.Status);
        Assert.Equal(JobStatus.Pending, persistedNewer.Status);
    }


    [Fact]
    public async Task GetAllForClient_ReturnsOnlyMatchingClientJobs()
    {
        var clientA = Guid.NewGuid();
        var clientB = Guid.NewGuid();
        await this._repo.AddAsync(MakeJob(clientA, prId: 1));
        await this._repo.AddAsync(MakeJob(clientB, prId: 2));
        await this._repo.AddAsync(MakeJob(clientA, prId: 3));

        var result = this._repo.GetAllForClient(clientA);
        Assert.Equal(2, result.Count);
        Assert.All(result, j => Assert.Equal(clientA, j.ClientId));
    }

    [Fact]
    public async Task GetAllJobsAsync_Pagination_Works()
    {
        for (var i = 1; i <= 5; i++)
        {
            await this._repo.AddAsync(MakeJob(prId: i));
        }

        var (total, page1) = await this._repo.GetAllJobsAsync(2, 0, null);
        Assert.Equal(5, total);
        Assert.Equal(2, page1.Count);

        var (_, page2) = await this._repo.GetAllJobsAsync(2, 2, null);
        Assert.Equal(2, page2.Count);

        var (_, page3) = await this._repo.GetAllJobsAsync(2, 4, null);
        Assert.Single(page3);
    }


    [Fact]
    public async Task GetAllJobsAsync_ReturnsAllJobsNewestFirst()
    {
        var job1 = MakeJob(prId: 100);
        await this._repo.AddAsync(job1);
        // Brief delay to ensure job2 gets a strictly later SubmittedAt timestamp.
        await Task.Delay(10);
        var job2 = MakeJob(prId: 101);
        await this._repo.AddAsync(job2);

        var (total, items) = await this._repo.GetAllJobsAsync(100, 0, null);
        Assert.Equal(2, total);
        // newest first — job2 was added last
        Assert.Equal(job2.Id, items[0].Id);
    }

    [Fact]
    public async Task GetAllJobsAsync_StatusFilter_ReturnsOnlyMatchingJobs()
    {
        var pending = MakeJob(prId: 200);
        var processing = MakeJob(prId: 201);
        await this._repo.AddAsync(pending);
        await this._repo.AddAsync(processing);
        await this._repo.TryTransitionAsync(processing.Id, JobStatus.Pending, JobStatus.Processing);

        var (total, items) = await this._repo.GetAllJobsAsync(100, 0, JobStatus.Processing);
        Assert.Equal(1, total);
        Assert.Equal(processing.Id, items[0].Id);
    }

    [Fact]
    public void GetById_UnknownId_ReturnsNull()
    {
        var result = this._repo.GetById(Guid.NewGuid());
        Assert.Null(result);
    }


    [Fact]
    public async Task GetPendingJobs_OrderedOldestFirst()
    {
        var job1 = MakeJob(prId: 10);
        var job2 = MakeJob(prId: 20);
        await this._repo.AddAsync(job1);
        await this._repo.AddAsync(job2);

        var pending = this._repo.GetPendingJobs();
        Assert.Equal(2, pending.Count);
        // oldest first — job1 was added first so SubmittedAt is earlier
        Assert.Equal(job1.Id, pending[0].Id);
    }

    [Fact]
    public async Task GetProcessingJobsAsync_EmptyWhenNoneProcessing()
    {
        await this._repo.AddAsync(MakeJob(prId: 400));
        var result = await this._repo.GetProcessingJobsAsync();
        Assert.Empty(result);
    }


    [Fact]
    public async Task GetProcessingJobsAsync_ReturnsOnlyProcessingJobs()
    {
        var j1 = MakeJob(prId: 300);
        var j2 = MakeJob(prId: 301);
        await this._repo.AddAsync(j1);
        await this._repo.AddAsync(j2);
        await this._repo.TryTransitionAsync(j1.Id, JobStatus.Pending, JobStatus.Processing);

        var result = await this._repo.GetProcessingJobsAsync();
        Assert.Single(result);
        Assert.Equal(j1.Id, result[0].Id);
    }


    [Fact]
    public async Task SetFailed_TransitionsToFailed()
    {
        var job = MakeJob();
        await this._repo.AddAsync(job);

        await this._repo.SetFailedAsync(job.Id, "ADO API error");

        var fetched = this._repo.GetById(job.Id);
        Assert.Equal(JobStatus.Failed, fetched!.Status);
        Assert.Equal("ADO API error", fetched.ErrorMessage);
        Assert.NotNull(fetched.CompletedAt);
    }


    [Fact]
    public async Task SetResult_TransitionsToCompleted()
    {
        var job = MakeJob();
        await this._repo.AddAsync(job);

        var result = new ReviewResult("Looks good", [new ReviewComment(null, null, CommentSeverity.Info, "No issues")]);
        await this._repo.SetResultAsync(job.Id, result);

        var fetched = this._repo.GetById(job.Id);
        Assert.Equal(JobStatus.Completed, fetched!.Status);
        Assert.NotNull(fetched.Result);
        Assert.Equal("Looks good", fetched.Result.Summary);
        Assert.NotNull(fetched.CompletedAt);
    }


    [Fact]
    public async Task TryTransition_PendingToProcessing_ReturnsTrue()
    {
        var job = MakeJob();
        await this._repo.AddAsync(job);

        var result = await this._repo.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing);
        Assert.True(result);

        var fetched = this._repo.GetById(job.Id);
        Assert.Equal(JobStatus.Processing, fetched!.Status);
        Assert.NotNull(fetched.ProcessingStartedAt);
    }

    [Fact]
    public async Task TryTransition_WrongFromStatus_ReturnsFalse()
    {
        var job = MakeJob();
        await this._repo.AddAsync(job);

        // job is Pending; trying to transition from Processing should fail
        var result = await this._repo.TryTransitionAsync(job.Id, JobStatus.Processing, JobStatus.Completed);
        Assert.False(result);

        var fetched = this._repo.GetById(job.Id);
        Assert.Equal(JobStatus.Pending, fetched!.Status);
    }

    [Fact]
    public async Task TryTransition_ProcessingToPending_ClosesOnlyOpenProtocolsAsAbandoned()
    {
        var job = MakeJob();
        await this._repo.AddAsync(job);
        await this._repo.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing);

        // Truncate to microsecond precision: PostgreSQL timestamptz stores microseconds, so a raw
        // DateTimeOffset (100 ns ticks) would not round-trip exactly and the "unchanged" assertion
        // below would fail on the lost sub-microsecond tick rather than on any real modification.
        var stampedAt = TruncateToMicroseconds(DateTimeOffset.UtcNow.AddMinutes(-5));
        var open1 = MakeProtocol(job.Id);
        var open2 = MakeProtocol(job.Id);
        var open3 = MakeProtocol(job.Id);
        var done1 = MakeProtocol(job.Id, stampedAt, "Completed");
        var done2 = MakeProtocol(job.Id, stampedAt, "Completed");
        this._dbContext.ReviewJobProtocols.AddRange(open1, open2, open3, done1, done2);
        await this._dbContext.SaveChangesAsync();
        // Detach so the bulk ExecuteUpdateAsync effect is observed via a fresh query, not the tracker.
        this._dbContext.ChangeTracker.Clear();

        var transitioned = await this._repo.TryTransitionAsync(job.Id, JobStatus.Processing, JobStatus.Pending);
        Assert.True(transitioned);

        var protocols = await this._dbContext.ReviewJobProtocols
            .AsNoTracking()
            .Where(p => p.JobId == job.Id)
            .ToListAsync();

        var openIds = new[] { open1.Id, open2.Id, open3.Id };
        foreach (var closed in protocols.Where(p => openIds.Contains(p.Id)))
        {
            Assert.NotNull(closed.CompletedAt);
            Assert.Equal("Abandoned", closed.Outcome);
        }

        var doneIds = new[] { done1.Id, done2.Id };
        foreach (var untouched in protocols.Where(p => doneIds.Contains(p.Id)))
        {
            Assert.Equal(stampedAt, untouched.CompletedAt);
            Assert.Equal("Completed", untouched.Outcome);
        }
    }

    [Fact]
    public async Task SetResult_ClosesOpenProtocolsAsAbandoned_AndCompletesJob()
    {
        var job = MakeJob();
        await this._repo.AddAsync(job);
        await this._repo.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing);

        var open1 = MakeProtocol(job.Id);
        var open2 = MakeProtocol(job.Id);
        this._dbContext.ReviewJobProtocols.AddRange(open1, open2);
        await this._dbContext.SaveChangesAsync();
        this._dbContext.ChangeTracker.Clear();

        await this._repo.SetResultAsync(job.Id, new ReviewResult("done", []));

        var protocols = await this._dbContext.ReviewJobProtocols
            .AsNoTracking()
            .Where(p => p.JobId == job.Id)
            .ToListAsync();
        Assert.Equal(2, protocols.Count);
        Assert.All(
            protocols, p =>
            {
                Assert.NotNull(p.CompletedAt);
                Assert.Equal("Abandoned", p.Outcome);
            });

        var fetched = this._repo.GetById(job.Id);
        Assert.Equal(JobStatus.Completed, fetched!.Status);
    }

    [Fact]
    public async Task TryTransition_PendingToProcessing_DoesNotCloseProtocols()
    {
        var job = MakeJob();
        await this._repo.AddAsync(job);

        var open1 = MakeProtocol(job.Id);
        this._dbContext.ReviewJobProtocols.Add(open1);
        await this._dbContext.SaveChangesAsync();
        this._dbContext.ChangeTracker.Clear();

        var transitioned = await this._repo.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing);
        Assert.True(transitioned);

        var protocol = await this._dbContext.ReviewJobProtocols
            .AsNoTracking()
            .SingleAsync(p => p.Id == open1.Id);
        Assert.Null(protocol.CompletedAt);
        Assert.Null(protocol.Outcome);
    }

    private static ReviewJobProtocol MakeProtocolWithTokens(Guid jobId, long inputTokens, long outputTokens)
    {
        return new ReviewJobProtocol
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            AttemptNumber = 1,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            Outcome = "Completed",
            TotalInputTokens = inputTokens,
            TotalOutputTokens = outputTokens,
        };
    }

    [Fact]
    public async Task GetJobListPageAsync_DoesNotMaterializeResultJson_AndSumsProtocolTokensInSql()
    {
        var job = MakeJob(prId: 900);
        await this._repo.AddAsync(job);
        await this._repo.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing);
        await this._repo.SetResultAsync(job.Id, new ReviewResult("sql-summary", []));

        // Run the projection through a context that logs its SQL so we can assert what it touches.
        var sql = new List<string>();
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .LogTo(sql.Add, [RelationalEventId.CommandExecuted])
            .Options;
        await using var loggingContext = new MeisterProPRDbContext(options);
        var loggingRepo = new JobRepository(loggingContext, new TestDbContextFactory(options), NullLogger<JobRepository>.Instance);

        var (_, items) = await loggingRepo.GetJobListPageAsync(100, 0, null);

        Assert.NotEmpty(items);
        var combined = string.Join("\n", sql);
        // The blob (summary + every comment) must never be selected by the overview query.
        Assert.DoesNotContain("result_json", combined);
        // The summary comes from its own denormalized column instead.
        Assert.Contains("result_summary", combined);
        // Token totals are a correlated SUM subquery, not a protocol-entity include.
        Assert.Contains("SUM(", combined.ToUpperInvariant());
    }

    [Fact]
    public async Task GetJobListPageAsync_ProjectsWithoutTrackingEntities()
    {
        var job = MakeJob(prId: 901);
        await this._repo.AddAsync(job);
        this._dbContext.ChangeTracker.Clear();

        var (_, items) = await this._repo.GetJobListPageAsync(100, 0, null);

        Assert.NotEmpty(items);
        Assert.Empty(this._dbContext.ChangeTracker.Entries());
    }

    [Fact]
    public async Task GetJobListPageAsync_NullAggregateAndNoProtocols_ReturnsZeroTokensNotNull()
    {
        var job = MakeJob(prId: 902);
        await this._repo.AddAsync(job);
        await this._repo.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing);
        await this._repo.SetResultAsync(job.Id, new ReviewResult("no-protocols", []));

        var (_, items) = await this._repo.GetJobListPageAsync(100, 0, null);
        var dto = items.Single(i => i.Id == job.Id);

        // SQL SUM over an empty set is null; the projection must coalesce to 0, matching the in-memory path.
        Assert.Equal(0L, dto.TotalInputTokens);
        Assert.Equal(0L, dto.TotalOutputTokens);
    }

    [Fact]
    public async Task GetJobListPageAsync_WithProtocolsAndNullAggregate_SumsProtocolTokens()
    {
        var job = MakeJob(prId: 903);
        await this._repo.AddAsync(job);
        this._dbContext.ReviewJobProtocols.AddRange(
            MakeProtocolWithTokens(job.Id, 100, 10),
            MakeProtocolWithTokens(job.Id, 200, 20));
        await this._dbContext.SaveChangesAsync();

        var (_, items) = await this._repo.GetJobListPageAsync(100, 0, null);
        var dto = items.Single(i => i.Id == job.Id);

        Assert.Equal(300L, dto.TotalInputTokens);
        Assert.Equal(30L, dto.TotalOutputTokens);
    }

    [Fact]
    public async Task GetJobListPageAsync_WithAggregateColumns_UsesAggregateOverProtocolSum()
    {
        var job = MakeJob(prId: 904);
        job.AccumulateTokens(1000, 500);
        await this._repo.AddAsync(job);
        // Protocols carry different token values; the aggregate columns must win.
        this._dbContext.ReviewJobProtocols.Add(MakeProtocolWithTokens(job.Id, 7, 7));
        await this._dbContext.SaveChangesAsync();

        var (_, items) = await this._repo.GetJobListPageAsync(100, 0, null);
        var dto = items.Single(i => i.Id == job.Id);

        Assert.Equal(1000L, dto.TotalInputTokens);
        Assert.Equal(500L, dto.TotalOutputTokens);
    }

    [Fact]
    public async Task GetJobListPageAsync_MatchesEntityPath_AcrossStates()
    {
        var completed = MakeJob(prId: 910);
        await this._repo.AddAsync(completed);
        await this._repo.TryTransitionAsync(completed.Id, JobStatus.Pending, JobStatus.Processing);
        await this._repo.SetResultAsync(completed.Id, new ReviewResult("done well", []));

        var emptySummary = MakeJob(prId: 911);
        await this._repo.AddAsync(emptySummary);
        await this._repo.TryTransitionAsync(emptySummary.Id, JobStatus.Pending, JobStatus.Processing);
        await this._repo.SetResultAsync(emptySummary.Id, new ReviewResult(string.Empty, []));

        var processing = MakeJob(prId: 912);
        await this._repo.AddAsync(processing);
        await this._repo.TryTransitionAsync(processing.Id, JobStatus.Pending, JobStatus.Processing);

        var failed = MakeJob(prId: 913);
        await this._repo.AddAsync(failed);
        await this._repo.SetFailedAsync(failed.Id, "kaboom");

        var (_, entities) = await this._repo.GetAllJobsAsync(100, 0, null);
        var (_, dtos) = await this._repo.GetJobListPageAsync(100, 0, null);

        // The projected DTO must carry byte-identical values to the full-entity path for every state.
        foreach (var entity in entities)
        {
            var dto = dtos.Single(d => d.Id == entity.Id);
            Assert.Equal(entity.Result?.Summary, dto.ResultSummary);
            Assert.Equal(entity.ErrorMessage, dto.ErrorMessage);
            Assert.Equal(entity.Status, dto.Status);
            Assert.Equal(entity.OrganizationUrl, dto.OrganizationUrl);
            Assert.Equal(entity.PullRequestId, dto.PullRequestId);
            var expectedInput = entity.TotalInputTokensAggregated ?? entity.Protocols.Sum(p => p.TotalInputTokens) ?? 0;
            var expectedOutput = entity.TotalOutputTokensAggregated ?? entity.Protocols.Sum(p => p.TotalOutputTokens) ?? 0;
            Assert.Equal(expectedInput, dto.TotalInputTokens);
            Assert.Equal(expectedOutput, dto.TotalOutputTokens);
        }
    }

    [Fact]
    public async Task SetResultAsync_PopulatesResultSummary()
    {
        var job = MakeJob(prId: 920);
        await this._repo.AddAsync(job);
        await this._repo.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing);
        await this._repo.SetResultAsync(job.Id, new ReviewResult("first run summary", []));

        var fetched = await this._dbContext.ReviewJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        Assert.Equal("first run summary", fetched.ResultSummary);
    }

    [Fact]
    public async Task SetResultAsync_Refinalize_UpdatesResultSummary()
    {
        // Restart and resume re-finalize the result through the same seam; the denormalized summary follows.
        var job = MakeJob(prId: 921);
        await this._repo.AddAsync(job);
        await this._repo.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing);
        await this._repo.SetResultAsync(job.Id, new ReviewResult("initial", []));
        await this._repo.SetResultAsync(job.Id, new ReviewResult("after resume", []));

        var fetched = await this._dbContext.ReviewJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        Assert.Equal("after resume", fetched.ResultSummary);
    }

    [Fact]
    public async Task BackfillSql_PopulatesResultSummaryFromPascalCaseSummaryKey()
    {
        var job = MakeJob(prId: 930);
        await this._repo.AddAsync(job);
        await this._repo.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing);
        await this._repo.SetResultAsync(job.Id, new ReviewResult("backfill me", []));

        // Simulate a pre-migration row: result_json is set but result_summary is still null.
        await this._dbContext.Database.ExecuteSqlRawAsync("UPDATE review_jobs SET result_summary = NULL WHERE id = {0}", job.Id);

        // The migration backfill expression: stored JSONB uses the PascalCase 'Summary' key.
        await this._dbContext.Database.ExecuteSqlRawAsync("UPDATE review_jobs SET result_summary = result_json ->> 'Summary' WHERE result_json IS NOT NULL;");

        var fetched = await this._dbContext.ReviewJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        Assert.Equal("backfill me", fetched.ResultSummary);
    }
}
