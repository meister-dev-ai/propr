// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Tests.Fixtures;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterProPR.Api.Tests;

/// <summary>
///     Integration tests verifying restart deduplication behaviour:
///     a PR with an existing Completed review job must not be re-enqueued on restart unless
///     the crawl can prove new same-iteration reviewer activity from persisted scan state.
/// </summary>
[Collection("PostgresApiIntegration")]
public sealed class PrCrawlRestartTests(PostgresContainerFixture fixture) : IAsyncLifetime
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

        // Wipe jobs so the count assertion is not polluted by other tests.
        var opts = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        await using var db = new MeisterProPRDbContext(opts);
        await db.ReviewJobs.ExecuteDeleteAsync();
    }

    /// <summary>
    ///     Seeds a Completed job for PR #42 directly into Postgres (before the app starts),
    ///     then starts the WebApplicationFactory (simulating a service restart).
    ///     Runs <see cref="IPrCrawlService.CrawlAsync" /> in a fresh scope with a stubbed
    ///     fetcher returning the same PR and asserts no duplicate job is created when there is
    ///     no persisted scan baseline proving new same-iteration reviewer activity.
    /// </summary>
    [SkippableFact]
    public async Task CrawlAsync_CompletedJobExistsAfterRestart_DoesNotCreateDuplicateJob()
    {
        fixture.SkipIfUnavailable();

        var connectionString = fixture.ConnectionString;

        // Step 1 — run migrations and seed the Completed job BEFORE the factory starts.
        // This prevents a race between the AdoPrCrawlerWorker's immediate startup crawl
        // and the test's own seeding step.
        var dbOptions = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(connectionString, o => o.UseVector())
            .Options;

        await using (var db = new MeisterProPRDbContext(dbOptions))
        {
            // Migrations already applied by PostgresContainerFixture.InitializeAsync().
            var repo = new JobRepository(db, new Fixtures.TestDbContextFactory(dbOptions));
            var job = new ReviewJob(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "https://dev.azure.com/org",
                "proj",
                "repo-42",
                42,
                1);
            await repo.AddAsync(job);
            await repo.SetResultAsync(job.Id, new ReviewResult("Looks good.", []));
        }

        // Step 2 — mock crawl-config repo returning one config for our org
        var crawlConfigRepo = Substitute.For<ICrawlConfigurationRepository>();
        var config = new CrawlConfigurationDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.com/org",
            "proj",
            Guid.NewGuid(),
            60,
            true,
            DateTimeOffset.UtcNow,
            []);
        crawlConfigRepo
            .GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>(new[] { config }));

        // Step 3 — mock fetcher returning PR #42 iteration 1
        var prFetcher = Substitute.For<IAssignedPrFetcher>();
        prFetcher
            .GetAssignedOpenPullRequestsAsync(Arg.Any<CrawlConfigurationDto>(), Arg.Any<CancellationToken>())
            .Returns(
                new List<AssignedPullRequestRef>
                {
                    new("https://dev.azure.com/org", "proj", "repo-42", 42, 1),
                });

        // Step 4 — start the factory (simulating service restart).
        //          The Completed job is already in the DB; the worker's initial crawl skips it.
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("DB_CONNECTION_STRING", connectionString);
                builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
                builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
                builder.UseSetting("MEISTER_ADMIN_KEY", "admin-key-min-16-chars-ok");
                builder.UseSetting("MEISTER_BOOTSTRAP_ADMIN_USER", "testadmin");
                builder.UseSetting("MEISTER_BOOTSTRAP_ADMIN_PASSWORD", "TestAdminPass1!");
                builder.UseSetting("MEISTER_JWT_SECRET", "test-jwt-secret-at-least-32-chars-ok!!");
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton(Substitute.For<IAdoTokenValidator>());
                    services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                    services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                    services.AddSingleton(prFetcher);
                });
            });

        _ = factory.CreateClient(); // triggers startup; migrations re-run (no-op), recovery runs

        // Step 5 — simulate the crawl worker running after restart by constructing
        // PrCrawlService directly with the mock config repo. This avoids a race
        // with the background AdoPrCrawlerWorker startup crawl, which uses the real
        // (empty) PostgresCrawlConfigurationRepository and is therefore a no-op.
        using (var scope = factory.Services.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();
            var crawlService = new PrCrawlService(
                crawlConfigRepo,
                prFetcher,
                jobs,
                Substitute.For<IPrStatusFetcher>(),
                Substitute.For<ILogger<PrCrawlService>>());
            await crawlService.CrawlAsync();
        }

        // Step 6 — assert no duplicate job was created.
        using (var scope = factory.Services.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();
            var (total, _) = await jobs.GetAllJobsAsync(100, 0, null);
            Assert.Equal(1, total);
        }
    }
}
