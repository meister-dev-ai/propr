// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Api.Tests.Fixtures;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests;

/// <summary>
///     Integration tests verifying startup recovery (A3 fix):
///     stale <see cref="JobStatus.Processing" /> jobs present in the database at startup
///     must be reset to <see cref="JobStatus.Pending" /> by the startup recovery logic in
///     <c>Program.cs</c> before the application starts serving requests.
/// </summary>
[Collection("PostgresApiIntegration")]
public sealed class StartupRecoveryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    public async Task DisposeAsync()
    {
    }

    public async Task InitializeAsync()
    {
        if (!fixture.IsAvailable)
        {
            return;
        }

        // Wipe jobs so a stale Processing job from a previous run doesn't interfere.
        var opts = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        await using var db = new MeisterProPRDbContext(opts);
        await db.ReviewJobs.ExecuteDeleteAsync();
    }

    /// <summary>
    ///     Directly inserts a <see cref="JobStatus.Processing" /> job into the database
    ///     (bypassing the app, simulating a job left over from a previous crash), then
    ///     starts a fresh <see cref="WebApplicationFactory{TEntryPoint}" /> pointing at the
    ///     same database. Asserts the job is now <see cref="JobStatus.Pending" /> after startup.
    /// </summary>
    [SkippableFact]
    public async Task Startup_ProcessingJobInDatabase_TransitionsJobToPending()
    {
        fixture.SkipIfUnavailable();

        var connectionString = fixture.ConnectionString;

        // Step 1 — prepare DB and seed a stale Processing job directly (pre-restart).
        // Migrations already applied by PostgresContainerFixture.InitializeAsync().
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(connectionString, o => o.UseVector())
            .Options;

        Guid stalJobId;
        await using (var db = new MeisterProPRDbContext(options))
        {
            var repo = new JobRepository(db, new TestDbContextFactory(options), NullLogger<JobRepository>.Instance);
            var job = new ReviewJob(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "https://dev.azure.com/org",
                "proj",
                "repo",
                99,
                1);
            await repo.AddAsync(job);
            await repo.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing);
            stalJobId = job.Id;
        }

        // Step 2 — start the application (simulates service restart)
        //          Startup recovery in Program.cs should transition the stale job to Pending.
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
                builder.UseSetting("DB_CONNECTION_STRING", connectionString);
                builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
                builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
                builder.UseSetting("MEISTER_ADMIN_KEY", "admin-key-min-16-chars-ok");
                builder.UseSetting("MEISTER_BOOTSTRAP_ADMIN_USER", "testadmin");
                builder.UseSetting("MEISTER_BOOTSTRAP_ADMIN_PASSWORD", "TestAdminPass1!");
                builder.UseSetting("MEISTER_JWT_SECRET", "test-jwt-secret-at-least-32-chars-ok!!");
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                    services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                    services.AddSingleton(Substitute.For<IAssignedReviewDiscoveryService>());
                });
            });

        _ = factory.CreateClient(); // triggers startup: migrations, bootstrap, recovery

        // Step 3 — assert the stale job is now Pending
        using var scope = factory.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var recoveredJob = jobs.GetById(stalJobId);

        Assert.NotNull(recoveredJob);
        Assert.Equal(JobStatus.Pending, recoveredJob.Status);
    }

    /// <summary>
    ///     A leased job is alive on another replica — or, once its lease expires, the counted reclaim
    ///     sweep's to take back — and a publishing job must never be requeued at all: a rolling deploy that
    ///     requeued it is how the same review got posted twice. Startup recovery only touches lease-less
    ///     rows.
    /// </summary>
    [SkippableFact]
    public async Task Startup_LeavesLeasedAndPublishingJobsToTheLeaseSubsystem()
    {
        fixture.SkipIfUnavailable();

        var connectionString = fixture.ConnectionString;
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(connectionString, o => o.UseVector())
            .Options;

        Guid leasedJobId;
        Guid publishingJobId;
        await using (var db = new MeisterProPRDbContext(options))
        {
            var repo = new JobRepository(db, new TestDbContextFactory(options), NullLogger<JobRepository>.Instance);
            var leaseStore = new Infrastructure.Features.Reviewing.Execution.Persistence.ReviewJobLeaseStore(db, repo);

            var leased = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 100, 1);
            await repo.AddAsync(leased);
            Assert.NotNull(await leaseStore.TryClaimAsync(leased.Id, "replica-a", TimeSpan.FromMinutes(10)));
            leasedJobId = leased.Id;

            var publishing = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://dev.azure.com/org", "proj", "repo", 101, 1);
            await repo.AddAsync(publishing);
            Assert.NotNull(await leaseStore.TryClaimAsync(publishing.Id, "replica-a", TimeSpan.FromMinutes(10)));
            Assert.True(await leaseStore.TryMarkPublishingAsync(publishing.Id));
            publishingJobId = publishing.Id;
        }

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
                builder.UseSetting("DB_CONNECTION_STRING", connectionString);
                builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
                builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
                builder.UseSetting("MEISTER_ADMIN_KEY", "admin-key-min-16-chars-ok");
                builder.UseSetting("MEISTER_BOOTSTRAP_ADMIN_USER", "testadmin");
                builder.UseSetting("MEISTER_BOOTSTRAP_ADMIN_PASSWORD", "TestAdminPass1!");
                builder.UseSetting("MEISTER_JWT_SECRET", "test-jwt-secret-at-least-32-chars-ok!!");
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                    services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                    services.AddSingleton(Substitute.For<IAssignedReviewDiscoveryService>());
                });
            });

        _ = factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();

        var leasedAfterBoot = jobs.GetById(leasedJobId);
        Assert.NotNull(leasedAfterBoot);
        Assert.Equal(JobStatus.Processing, leasedAfterBoot.Status);
        Assert.Equal("replica-a", leasedAfterBoot.LeaseOwner);

        var publishingAfterBoot = jobs.GetById(publishingJobId);
        Assert.NotNull(publishingAfterBoot);
        Assert.Equal(JobStatus.Processing, publishingAfterBoot.Status);
        Assert.NotNull(publishingAfterBoot.PublishingStartedAt);
    }
}
