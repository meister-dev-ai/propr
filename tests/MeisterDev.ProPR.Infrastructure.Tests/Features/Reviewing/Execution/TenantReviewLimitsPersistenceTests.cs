// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Services;
using MeisterDev.ProPR.Application.Options;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Execution.Persistence;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Runner.Contracts;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using FactAttribute = Xunit.SkippableFactAttribute;
using TheoryAttribute = Xunit.SkippableTheoryAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Reviewing.Execution;

/// <summary>
///     The limits a tenant sets on its reviews, against a real PostgreSQL instance. The cap on concurrent reviews is
///     decided by the database under concurrent claimants, so an in-memory double would prove nothing about it.
/// </summary>
[Collection("PostgresIntegration2")]
public sealed class TenantReviewLimitsPersistenceTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    private static readonly Microsoft.Extensions.Options.IOptions<ReviewLeaseOptions> LeaseOptions =
        Microsoft.Extensions.Options.Options.Create(new ReviewLeaseOptions());

    private DbContextOptions<MeisterProPRDbContext> _options = null!;
    private MeisterProPRDbContext _dbContext = null!;
    private JobRepository _repo = null!;
    private ReviewJobLeaseStore _store = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        this._options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(this._options);

        // The installation-wide count covers every review job, so no job of another test may be left processing.
        await this._dbContext.ReviewJobs.ExecuteDeleteAsync();
        this._repo = this.CreateRepository(this._dbContext);
        this._store = this.CreateStore(this._dbContext);
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is not null)
        {
            await this._dbContext.DisposeAsync();
        }
    }

    // The acceptance criterion: a tenant with a cap of one never has two review jobs in processing, whichever
    // hosts claim its jobs at the same time, while the jobs of another tenant are claimed beside them. The race
    // runs several rounds, because one round can pass by luck.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClaimantsRacingForTheJobsOfATenantWithACapOfOne_NeverRunTwoOfThem(bool underAnInstallationCap)
    {
        var capped = await this.AddClientAsync(await this.AddTenantAsync(maxConcurrentReviews: 1));
        var uncapped = await this.AddClientAsync(await this.AddTenantAsync(maxConcurrentReviews: null));

        for (var round = 0; round < 5; round++)
        {
            await this._dbContext.ReviewJobs.ExecuteDeleteAsync();
            var cappedJobs = new List<ReviewJob>();
            for (var prId = 1; prId <= 8; prId++)
            {
                cappedJobs.Add(await this.AddPendingJobAsync(capped, prId));
            }

            var otherJobs = new[] { await this.AddPendingJobAsync(uncapped, 101), await this.AddPendingJobAsync(uncapped, 102) };

            var claims = await this.ClaimConcurrentlyAsync(
                [.. cappedJobs, .. otherJobs],
                async (store, jobId, index) => underAnInstallationCap
                    ? (await store.TryClaimWithinProcessingCapAsync(jobId, $"host-{index}", LeaseDuration, 100)).Lease
                    : await store.TryClaimAsync(jobId, $"host-{index}", LeaseDuration));

            Assert.Single(claims, claim => claim.Lease is not null && cappedJobs.Any(job => job.Id == claim.JobId));
            Assert.Equal(1, await this.CountProcessingAsync(capped));
            Assert.Equal(2, await this.CountProcessingAsync(uncapped));
        }
    }

    // Several runners of the tenant ask for work at the same moment, each served by its own control-plane
    // request. One of them receives a job; the others are told there is nothing for them, and the runner of
    // another tenant is served as usual.
    [Fact]
    public async Task RunnersLeasingAtOnce_ReceiveOneJobOfATenantWithACapOfOne()
    {
        var cappedTenant = await this.AddTenantAsync(maxConcurrentReviews: 1);
        var otherTenant = await this.AddTenantAsync(maxConcurrentReviews: null);
        var capped = await this.AddClientAsync(cappedTenant);
        var other = await this.AddClientAsync(otherTenant);
        for (var prId = 1; prId <= 4; prId++)
        {
            await this.AddPendingJobAsync(capped, prId);
        }

        var otherJob = await this.AddPendingJobAsync(other, 101);

        var runners = Enumerable.Range(0, 4).Select(_ => MakeRunner(cappedTenant)).Append(MakeRunner(otherTenant)).ToList();
        var registry = Substitute.For<IRunnerRegistry>();
        foreach (var runner in runners)
        {
            registry.FindByIdAsync(runner.Id, Arg.Any<CancellationToken>()).Returns(runner);
        }

        var contexts = runners.Select(_ => new MeisterProPRDbContext(this._options)).ToList();
        try
        {
            var offers = await Task.WhenAll(
                runners.Select((runner, index) => this.CreateOfferService(contexts[index], registry)
                    .OfferAsync(new RunnerLeaseRequest(runner.Id, 1, RunnerContractVersion.Current))));

            var cappedOffers = offers.Take(4).ToList();
            Assert.Single(cappedOffers, offer => offer.Granted);
            Assert.All(
                cappedOffers.Where(offer => !offer.Granted),
                offer => Assert.Equal(RunnerLeaseRefusal.NoMatchingWork, offer.Refusal));
            Assert.Equal(otherJob.Id, offers[4].Manifest?.JobId);
            Assert.Equal(1, await this.CountProcessingAsync(capped));
        }
        finally
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task AClaimForATenantAtItsCap_NamesTheTenantAndLeavesTheJobPending()
    {
        var tenantId = await this.AddTenantAsync(maxConcurrentReviews: 2);
        var client = await this.AddClientAsync(tenantId);
        Assert.NotNull(await this._store.TryClaimAsync((await this.AddPendingJobAsync(client, 1)).Id, "host-a", LeaseDuration));
        Assert.NotNull(await this._store.TryClaimAsync((await this.AddPendingJobAsync(client, 2)).Id, "host-b", LeaseDuration));
        var queued = await this.AddPendingJobAsync(client, 3);

        var capped = await this._store.TryClaimWithinProcessingCapAsync(queued.Id, "host-c", LeaseDuration, 10);
        var uncapped = await this._store.TryClaimAsync(queued.Id, "host-c", LeaseDuration);

        Assert.Equal(ReviewJobCappedClaimOutcome.TenantAtCapacity, capped.Outcome);
        Assert.Equal(tenantId, capped.TenantId);
        Assert.Equal(2, capped.Cap);
        Assert.Equal(2, capped.ProcessingCount);
        Assert.Null(capped.Lease);
        Assert.Null(uncapped);
        Assert.Equal(JobStatus.Pending, (await this.ReadJobAsync(queued.Id)).Status);
    }

    // Only a timeout on one of the admission locks is a statement about capacity. A claim whose statement waits
    // behind a table lock, as a migration holds one, reports the database error, not a capacity it never measured.
    [Fact]
    public async Task AClaimWaitingBehindATableLock_ReportsTheDatabaseError()
    {
        var client = await this.AddClientAsync(await this.AddTenantAsync(maxConcurrentReviews: 1));
        var job = await this.AddPendingJobAsync(client, 1);

        await using (var locker = new MeisterProPRDbContext(this._options))
        {
            await using var lockTransaction = await locker.Database.BeginTransactionAsync();
            await locker.Database.ExecuteSqlRawAsync("LOCK TABLE review_jobs IN SHARE MODE");

            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration));

            Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
        }

        Assert.Equal(JobStatus.Pending, (await this.ReadJobAsync(job.Id)).Status);
    }

    // The installation's ceiling is the wider limit, so a full installation is reported as such even when the
    // tenant of the job has room.
    [Fact]
    public async Task AFullInstallation_IsReportedAsTheInstallationsCapacity()
    {
        var client = await this.AddClientAsync(await this.AddTenantAsync(maxConcurrentReviews: 5));
        Assert.NotNull(await this._store.TryClaimAsync((await this.AddPendingJobAsync(client, 1)).Id, "host-a", LeaseDuration));
        var queued = await this.AddPendingJobAsync(client, 2);

        var claim = await this._store.TryClaimWithinProcessingCapAsync(queued.Id, "host-b", LeaseDuration, 1);

        Assert.Equal(ReviewJobCappedClaimOutcome.AtCapacity, claim.Outcome);
        Assert.Equal(1, claim.Cap);
        Assert.Null(claim.TenantId);
    }

    [Fact]
    public async Task ATenantAtItsCap_ClaimsAgainOnceARunningReviewEnds()
    {
        var client = await this.AddClientAsync(await this.AddTenantAsync(maxConcurrentReviews: 1));
        var running = await this.AddPendingJobAsync(client, 1);
        var queued = await this.AddPendingJobAsync(client, 2);
        Assert.NotNull(await this._store.TryClaimAsync(running.Id, "host-a", LeaseDuration));
        Assert.Null(await this._store.TryClaimAsync(queued.Id, "host-b", LeaseDuration));

        await this._repo.SetResultAsync(running.Id, new ReviewResult("Done", []));

        Assert.NotNull(await this._store.TryClaimAsync(queued.Id, "host-b", LeaseDuration));
    }

    // Thread passes and mention replies are stored apart from review jobs, and the cap counts review jobs only.
    [Fact]
    public async Task AMentionReplyThatIsBeingAnswered_DoesNotCountAgainstTheCap()
    {
        var client = await this.AddClientAsync(await this.AddTenantAsync(maxConcurrentReviews: 1));

        // The mention coordinates are unique per run, because the table refuses a second job for one mention and
        // other tests of this collection write mention jobs too.
        var mention = new MentionReplyJob(
            Guid.NewGuid(), client, "https://dev.azure.com/limits", "proj", "repo", 1,
            Random.Shared.Next(1_000_000, 9_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture), 100,
            "what does this do?")
        {
            Status = MentionJobStatus.Processing,
            ProcessingStartedAt = DateTimeOffset.UtcNow,
        };
        this._dbContext.MentionReplyJobs.Add(mention);
        await this._dbContext.SaveChangesAsync();
        try
        {
            var job = await this.AddPendingJobAsync(client, 1);

            Assert.NotNull(await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration));
        }
        finally
        {
            await this._dbContext.MentionReplyJobs.Where(row => row.Id == mention.Id).ExecuteDeleteAsync();
        }
    }

    // The window a host reads is bounded. The backlog of a full tenant is left out of it, so the job of another
    // tenant behind that backlog is still among the candidates.
    [Fact]
    public async Task TheBacklogOfAFullTenant_DoesNotHideTheJobsOfOtherTenants()
    {
        var full = await this.AddClientAsync(await this.AddTenantAsync(maxConcurrentReviews: 1));
        var other = await this.AddClientAsync(await this.AddTenantAsync(maxConcurrentReviews: 1));
        Assert.NotNull(await this._store.TryClaimAsync((await this.AddPendingJobAsync(full, 1)).Id, "host-a", LeaseDuration));
        for (var prId = 2; prId <= 4; prId++)
        {
            await this.AddPendingJobAsync(full, prId, DateTimeOffset.UtcNow.AddMinutes(-30 + prId));
        }

        var behind = await this.AddPendingJobAsync(other, 101, DateTimeOffset.UtcNow);

        var candidates = await this._store.GetClaimCandidatesAsync(2);

        Assert.Equal([behind.Id], candidates.Select(job => job.Id));
    }

    [Fact]
    public async Task TheBacklogOfAFullTenant_IsOfferedToNoRunner()
    {
        var tenantId = await this.AddTenantAsync(maxConcurrentReviews: 1);
        var client = await this.AddClientAsync(tenantId);
        Assert.NotNull(await this._store.TryClaimAsync((await this.AddPendingJobAsync(client, 1)).Id, "host-a", LeaseDuration));
        var waiting = await this.AddPendingJobAsync(client, 2);
        var offers = new RunnerLeaseOfferStore(this._dbContext);

        Assert.Empty(await offers.GetOfferCandidatesAsync(tenantId, [], [], 50));

        await this._dbContext.Tenants.Where(tenant => tenant.Id == tenantId)
            .ExecuteUpdateAsync(set => set.SetProperty(tenant => tenant.MaxConcurrentReviews, 2));

        Assert.Equal([waiting.Id], (await offers.GetOfferCandidatesAsync(tenantId, [], [], 50)).Select(job => job.Id));
    }

    // The deadline is the start of processing plus the tenant's duration, and the time left is measured on the
    // clock that stamped the start.
    [Fact]
    public async Task TheTimeLimitReader_MeasuresTheTimeLeftFromTheStartOfProcessing()
    {
        var client = await this.AddClientAsync(await this.AddTenantAsync(maxConcurrentReviews: null, maxReviewDurationSeconds: 600));
        var job = await this.AddPendingJobAsync(client, 1);
        var reader = new ReviewTimeLimitReader(this._dbContext);

        Assert.Null(await reader.ReadAsync(job.Id));

        Assert.NotNull(await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration));
        var fresh = await reader.ReadAsync(job.Id);

        Assert.NotNull(fresh);
        Assert.Equal(600, fresh.MaxReviewDurationSeconds);
        Assert.InRange(fresh.Remaining.TotalSeconds, 540, 600);

        await this._dbContext.Database.ExecuteSqlRawAsync(
            "UPDATE review_jobs SET processing_started_at = now() - make_interval(secs => 700) WHERE id = {0}",
            [job.Id]);
        var passed = await reader.ReadAsync(job.Id);

        Assert.NotNull(passed);
        Assert.InRange(passed.Remaining.TotalSeconds, -160, -100);
    }

    [Fact]
    public async Task TheTimeLimitReader_ReadsNothingForATenantWithoutADuration()
    {
        var client = await this.AddClientAsync(await this.AddTenantAsync(maxConcurrentReviews: 1));
        var job = await this.AddPendingJobAsync(client, 1);
        Assert.NotNull(await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration));

        Assert.Null(await new ReviewTimeLimitReader(this._dbContext).ReadAsync(job.Id));
    }

    // A review that ended at the time limit completes, with the reason on the job and the files it left out in
    // its stored result. A later run that reviews everything leaves no reason behind.
    [Fact]
    public async Task AReviewThatEndedAtTheTimeLimit_CompletesWithTheReasonRecorded()
    {
        var job = await this.AddPendingJobAsync(Guid.NewGuid(), 1);
        Assert.NotNull(await this._store.TryClaimAsync(job.Id, "host-a", LeaseDuration));

        await this._repo.SetResultAsync(
            job.Id,
            new ReviewResult("Partial review", [])
            {
                TimeLimitReached = true,
                TimeLimitSeconds = 600,
                TimeLimitSkippedFilePaths = ["src/Late.cs"],
            });

        var stored = await this.ReadJobAsync(job.Id);
        Assert.Equal(JobStatus.Completed, stored.Status);
        Assert.Equal(ReviewJobCompletionReason.TimeLimitReached, stored.CompletionReason);
        Assert.Equal(ReviewJobFailureReason.Unspecified, stored.FailureReason);
        Assert.True(stored.Result!.TimeLimitReached);
        Assert.Equal(600, stored.Result.TimeLimitSeconds);
        Assert.Equal(["src/Late.cs"], stored.Result.TimeLimitSkippedFilePaths);

        await this._repo.SetResultAsync(job.Id, new ReviewResult("Full review", []));

        Assert.Equal(ReviewJobCompletionReason.None, (await this.ReadJobAsync(job.Id)).CompletionReason);
    }

    private static ReviewRunner MakeRunner(Guid tenantId)
    {
        return new ReviewRunner(
            Guid.NewGuid(),
            tenantId,
            "runner",
            [],
            RunnerContractVersion.Current,
            "hashed:secret",
            $"LOOKUP-{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow.AddDays(30),
            DateTimeOffset.UtcNow);
    }

    private static RunnerJobManifest MakeManifest(RunnerJobManifestRequest request)
    {
        return new RunnerJobManifest(
            RunnerContractVersion.Current,
            request.Job.Id,
            request.Job.ClientId,
            request.Lease.Generation,
            new RunnerReviewTarget(
                "azuredevops", "https://dev.azure.com/x", "project", "repo", "repo", "1", 1, 1,
                "title", null, "feature", "main", "head", "base", [], []),
            new RunnerWorkspaceReference(request.WorkspaceFetchPath, "head", "base", request.MaxWorkspaceTransferBytes),
            new RunnerModelBinding("reviewer", "model", "OpenAi", "None", null, null, null, false, true, true),
            [],
            new RunnerPromptConfiguration(null, null, new Dictionary<string, string>()),
            [],
            [],
            null,
            new RunnerTraceContext(string.Empty, null));
    }

    private RunnerLeaseOfferService CreateOfferService(MeisterProPRDbContext context, IRunnerRegistry registry)
    {
        var preparer = Substitute.For<IRunnerJobDispatchPreparer>();
        preparer.PrepareAsync(Arg.Any<ReviewJob>(), Arg.Any<ReviewJobLease>(), Arg.Any<CancellationToken>())
            .Returns(call => RunnerJobDispatchPreparation.Ready(
                new RunnerJobManifestRequest(call.Arg<ReviewJob>(), call.Arg<ReviewJobLease>(), "main", [], "path", 1024)));
        var manifests = Substitute.For<IRunnerJobManifestResolver>();
        manifests.ResolveAsync(Arg.Any<RunnerJobManifestRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => RunnerJobManifestResolution.Resolved(MakeManifest(call.Arg<RunnerJobManifestRequest>())));

        return new RunnerLeaseOfferService(
            registry,
            new RunnerLeaseOfferStore(context),
            this.CreateStore(context),
            preparer,
            manifests,
            LeaseOptions,
            new RunnerJobBudgetRegistry(),
            new RunnerJobToolsRegistry(),
            new RunnerWorkspaceRegistry(),
            NullLogger<RunnerLeaseOfferService>.Instance);
    }

    /// <summary>Claims every job at the same time, each from its own context, as separate hosts would.</summary>
    private async Task<IReadOnlyList<(Guid JobId, ReviewJobLease? Lease)>> ClaimConcurrentlyAsync(
        IReadOnlyList<ReviewJob> jobs,
        Func<ReviewJobLeaseStore, Guid, int, Task<ReviewJobLease?>> claim)
    {
        var contexts = jobs.Select(_ => new MeisterProPRDbContext(this._options)).ToList();
        try
        {
            using var start = new SemaphoreSlim(0, jobs.Count);
            var claims = jobs.Select(async (job, index) =>
            {
                var store = this.CreateStore(contexts[index]);
                await start.WaitAsync();
                return (job.Id, await claim(store, job.Id, index));
            }).ToList();
            start.Release(jobs.Count);
            return await Task.WhenAll(claims);
        }
        finally
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync();
            }
        }
    }

    private async Task<int> CountProcessingAsync(Guid clientId)
    {
        await using var context = new MeisterProPRDbContext(this._options);
        return await context.ReviewJobs.CountAsync(job => job.ClientId == clientId && job.Status == JobStatus.Processing);
    }

    private async Task<Guid> AddTenantAsync(int? maxConcurrentReviews, int? maxReviewDurationSeconds = null)
    {
        var tenantId = Guid.NewGuid();
        this._dbContext.Tenants.Add(
            new TenantRecord
            {
                Id = tenantId,
                Slug = $"t{tenantId:N}",
                DisplayName = "Limited tenant",
                MaxConcurrentReviews = maxConcurrentReviews,
                MaxReviewDurationSeconds = maxReviewDurationSeconds,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        await this._dbContext.SaveChangesAsync();
        return tenantId;
    }

    private async Task<Guid> AddClientAsync(Guid tenantId)
    {
        var clientId = Guid.NewGuid();
        this._dbContext.Clients.Add(
            new ClientRecord { Id = clientId, TenantId = tenantId, DisplayName = "Limited client", IsActive = true, CreatedAt = DateTimeOffset.UtcNow });
        await this._dbContext.SaveChangesAsync();
        return clientId;
    }

    private async Task<ReviewJob> AddPendingJobAsync(Guid clientId, int prId, DateTimeOffset? submittedAt = null)
    {
        var job = new ReviewJob(Guid.NewGuid(), clientId, "https://dev.azure.com/org", "proj", "repo", prId, 1);
        await this._repo.AddAsync(job);

        // The order of the candidates follows submitted_at, so a test that needs an order writes the value instead
        // of sleeping between inserts.
        if (submittedAt is { } at)
        {
            await this._dbContext.Database.ExecuteSqlRawAsync("UPDATE review_jobs SET submitted_at = {0} WHERE id = {1}", at, job.Id);
        }

        return job;
    }

    private async Task<ReviewJob> ReadJobAsync(Guid id)
    {
        await using var context = new MeisterProPRDbContext(this._options);
        return await context.ReviewJobs.AsNoTracking().SingleAsync(j => j.Id == id);
    }

    private JobRepository CreateRepository(MeisterProPRDbContext context)
    {
        return new JobRepository(context, new TestDbContextFactory(this._options), NullLogger<JobRepository>.Instance);
    }

    private ReviewJobLeaseStore CreateStore(MeisterProPRDbContext context)
    {
        return new ReviewJobLeaseStore(context, this.CreateRepository(context), LeaseOptions, NullLogger<ReviewJobLeaseStore>.Instance);
    }
}
