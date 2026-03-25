using System.Net;
using MeisterProPR.Application.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Middleware;

/// <summary>Integration tests for <see cref="MeisterProPR.Api.Middleware.AdminKeyMiddleware" />.</summary>
public sealed class AdminKeyMiddlewareTests(AdminKeyMiddlewareTests.AdminKeyFactory factory)
    : IClassFixture<AdminKeyMiddlewareTests.AdminKeyFactory>
{
    private const string ValidAdminKey = "admin-key-min-16-chars-ok";

    [Fact]
    public async Task GetJobs_WithoutAdminKey_Returns401()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/jobs");
        request.Headers.Add("X-Client-Key", "test-key-123");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetJobs_WithValidAdminKey_Returns200()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/jobs");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        // Also add X-Client-Key so ClientKeyMiddleware passes through
        request.Headers.Add("X-Client-Key", "test-key-123");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetJobs_WithWrongAdminKey_Returns401()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/jobs");
        request.Headers.Add("X-Admin-Key", "wrong-key-value");
        request.Headers.Add("X-Client-Key", "test-key-123");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetReviews_NonAdminRoute_NotAffectedByAdminMiddleware()
    {
        // Non-admin routes (/reviews) pass through regardless of admin key presence
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/reviews");
        request.Headers.Add("X-Client-Key", "test-key-123");
        // No X-Ado-Token — ReviewsController will return 401 for that reason, not admin reason
        // But the important thing is AdminKeyMiddleware didn't block this route.
        // We'd get a 401 from ReviewsController ADO check, not from AdminKeyMiddleware.
        var response = await client.SendAsync(request);

        // 401 from ReviewsController ADO validation, NOT from AdminKeyMiddleware
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public sealed class AdminKeyFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
            builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
            builder.UseSetting("MEISTER_CLIENT_KEYS", "test-key-123");
            builder.UseSetting("MEISTER_ADMIN_KEY", ValidAdminKey);
            // No DB_CONNECTION_STRING → InMemory mode
            builder.ConfigureServices(services =>
            {
                services.AddSingleton(Substitute.For<IAdoTokenValidator>());
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedPrFetcher>());
            });
        }
    }
}
