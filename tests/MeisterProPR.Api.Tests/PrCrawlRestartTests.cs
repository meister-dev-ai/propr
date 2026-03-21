using MeisterProPR.Api.Tests.Fixtures;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterProPR.Api.Tests;

/// <summary>
///     Integration tests verifying restart-idempotency (A2 fix):
///     a PR with an existing Completed review job must NOT trigger a second job
///     after service restart (i.e. in a fresh DI scope backed by the same Postgres DB).
/// </summary>
[Collection("PostgresApiIntegration")]
public sealed class PrCrawlRestartTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    public async Task DisposeAsync()
    {
    }

    public async Task InitializeAsync()
    {
        // Wipe jobs so the count assertion (total == 1) is not polluted by other tests.
        var opts = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .Options;
        await using var db = new MeisterProPRDbContext(opts);
        await db.ReviewJobs.ExecuteDeleteAsync();
    }

    /// <summary>
    ///     Seeds a Completed job for PR #42 directly into Postgres (before the app starts),
    ///     then starts the WebApplicationFactory (simulating a service restart).
    ///     Runs <see cref="IPrCrawlService.CrawlAsync" /> in a fresh scope with a stubbed
    ///     fetcher returning the same PR and asserts the total job count stays at 1.
    /// </summary>
    [Fact]
    public async Task CrawlAsync_CompletedJobExistsAfterRestart_DoesNotCreateDuplicate()
    {
        var connectionString = fixture.ConnectionString;

        // Step 1 — run migrations and seed the Completed job BEFORE the factory starts.
        // This prevents a race between the AdoPrCrawlerWorker's immediate startup crawl
        // and the test's own seeding step.
        var dbOptions = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using (var db = new MeisterProPRDbContext(dbOptions))
        {
            // Migrations already applied by PostgresContainerFixture.InitializeAsync().
            var repo = new PostgresJobRepository(db);
            var job = new ReviewJob(
                Guid.NewGuid(),
                null,
                "https://dev.azure.com/org",
                "proj",
                "repo-42",
                42,
                1);
            repo.Add(job);
            repo.SetResult(job.Id, new ReviewResult("Looks good.", []));
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
            DateTimeOffset.UtcNow);
        crawlConfigRepo
            .GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>(new[] { config }));

        // Step 3 — mock fetcher returning PR #42 iteration 1
        var prFetcher = Substitute.For<IAssignedPullRequestFetcher>();
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
                builder.ConfigureServices(services =>
                {
                    services.AddSingleton(Substitute.For<IAdoTokenValidator>());
                    services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                    services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                    services.AddSingleton(prFetcher);
                    services.AddSingleton(crawlConfigRepo);
                });
            });

        _ = factory.CreateClient(); // triggers startup; migrations re-run (no-op), recovery runs

        // Step 5 — fresh scope simulates the crawl worker running after restart
        using (var scope = factory.Services.CreateScope())
        {
            var crawlService = scope.ServiceProvider.GetRequiredService<IPrCrawlService>();
            await crawlService.CrawlAsync();
        }

        // Step 6 — assert total is still 1; no duplicate was created
        using (var scope = factory.Services.CreateScope())
        {
            var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();
            var (total, _) = await jobs.GetAllJobsAsync(100, 0, null);
            Assert.Equal(1, total);
        }
    }
}
