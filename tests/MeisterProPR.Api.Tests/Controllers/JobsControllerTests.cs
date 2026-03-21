using System.Diagnostics;
using System.Net;
using System.Text.Json;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Controllers;

/// <summary>Integration tests for <see cref="MeisterProPR.Api.Controllers.JobsController" />.</summary>
public sealed class JobsControllerTests(JobsControllerTests.JobsApiFactory factory)
    : IClassFixture<JobsControllerTests.JobsApiFactory>
{
    private const string ValidAdminKey = "admin-key-min-16-chars-ok";

    // ── Pagination ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetJobs_DefaultPagination_Returns200WithTotalAndItems()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/jobs?limit=10&offset=0");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Headers.Add("X-Client-Key", "test-key-123");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.TryGetProperty("total", out _));
        Assert.True(body.RootElement.TryGetProperty("items", out _));
    }

    // ── S1 fix: clientId in response, not clientKey ───────────────────────────

    [Fact]
    public async Task GetJobs_ResponseItems_DoNotExposeRawClientKey()
    {
        // S1 fix: ensure the raw client key string is never in the response
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/jobs");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Headers.Add("X-Client-Key", "test-key-123");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("clientKey", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("test-key-123", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetJobs_StatusFilter_ReturnsOnlyMatchingJobs()
    {
        var repo = factory.Services.GetRequiredService<IJobRepository>();
        var completedJob = new ReviewJob(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "https://dev.azure.com/org",
            "proj",
            "repo",
            500,
            1);
        repo.Add(completedJob);
        repo.TryTransition(completedJob.Id, JobStatus.Pending, JobStatus.Processing);
        repo.SetResult(completedJob.Id, new ReviewResult("done", []));

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/jobs?status=Completed");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Headers.Add("X-Client-Key", "test-key-123");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = body.RootElement.GetProperty("items");
        Assert.True(items.GetArrayLength() >= 1);
        foreach (var item in items.EnumerateArray())
        {
            Assert.Equal("completed", item.GetProperty("status").GetString());
        }
    }

    // ── Performance: SC-004 / T048 ────────────────────────────────────────────

    /// <summary>
    ///     Verifies GET /jobs?limit=100 responds in under 2 seconds even with 10,000 seeded jobs.
    ///     Documents that the ix_review_jobs_status index (or equivalent) must be active in DB mode.
    ///     Note: This test uses InMemoryJobRepository so measures in-memory path performance only;
    ///     DB-mode performance is covered by the PostgreSQL integration tests.
    /// </summary>
    [Fact]
    public async Task GetJobs_With10kJobs_RespondsUnder2Seconds()
    {
        // Seed 10,000 jobs via IJobRepository
        using var scope = factory.Services.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        for (var i = 0; i < 10_000; i++)
        {
            jobRepo.Add(
                new ReviewJob(
                    Guid.NewGuid(),
                    null,
                    "https://dev.azure.com/org",
                    "proj",
                    "repo",
                    i + 1,
                    1));
        }

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/jobs?limit=100&offset=0");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Headers.Add("X-Client-Key", "test-key-123");

        var sw = Stopwatch.StartNew();
        var response = await client.SendAsync(request);
        sw.Stop();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(
            sw.Elapsed < TimeSpan.FromSeconds(2),
            $"GET /jobs?limit=100 took {sw.Elapsed.TotalMilliseconds:F0}ms — expected < 2000ms");
    }

    [Fact]
    public async Task GetJobs_WithoutAdminKey_Returns401()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/jobs");
        request.Headers.Add("X-Client-Key", "test-key-123");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ── Authorization ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetJobs_WithValidAdminKey_Returns200()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/jobs");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Headers.Add("X-Client-Key", "test-key-123");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetJobs_WithWrongAdminKey_Returns401()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/jobs");
        request.Headers.Add("X-Admin-Key", "wrong-key-here");
        request.Headers.Add("X-Client-Key", "test-key-123");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public sealed class JobsApiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
            builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
            builder.UseSetting("MEISTER_CLIENT_KEYS", "test-key-123");
            builder.UseSetting("MEISTER_ADMIN_KEY", ValidAdminKey);
            // No DB_CONNECTION_STRING → InMemory mode (InMemoryJobRepository)
            builder.ConfigureServices(services =>
            {
                services.AddSingleton(Substitute.For<IAdoTokenValidator>());
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedPullRequestFetcher>());
            });
        }
    }
}
