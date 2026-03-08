using System.Net;
using System.Text.Json;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Controllers;

public class ReviewsControllerListTests(ReviewsControllerListTests.ListReviewsFactory factory)
    : IClassFixture<ReviewsControllerListTests.ListReviewsFactory>
{
    private static HttpRequestMessage CreateListRequest(string clientKey = "test-key-123")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/reviews");
        request.Headers.Add("X-Client-Key", clientKey);
        request.Headers.Add("X-Ado-Token", "valid-ado-token");
        return request;
    }

    [Fact]
    public async Task ListReviews_EmptyRepository_Returns200WithEmptyArray()
    {
        var client = factory.CreateClient();
        using var request = CreateListRequest();

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);
    }

    [Fact]
    public async Task ListReviews_NoAdoToken_Returns401()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/reviews");
        request.Headers.Add("X-Client-Key", "test-key-123");
        // No X-Ado-Token

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ListReviews_WithJobs_ReturnsNewestFirst()
    {
        var client = factory.CreateClient();
        factory.InsertJob("test-key-123", prId: 701);
        factory.InsertJob("test-key-123", prId: 702);

        using var request = CreateListRequest("test-key-123");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);

        var items = body.RootElement.EnumerateArray().ToList();
        Assert.True(items.Count >= 2);

        // Verify newest first: submittedAt of item[0] >= item[1]
        var first = items[0].GetProperty("submittedAt").GetDateTimeOffset();
        var second = items[1].GetProperty("submittedAt").GetDateTimeOffset();
        Assert.True(first >= second);
    }

    [Fact]
    public async Task ListReviews_ClientKeyScoping_JobsFromOneKeyNotVisibleToAnother()
    {
        var client = factory.CreateClient();
        factory.InsertJob("test-key-123", prId: 800);

        // test-key-456 is also a valid key but should not see test-key-123's jobs
        using var request = CreateListRequest("test-key-456");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = body.RootElement.EnumerateArray().ToList();

        // test-key-456 should not see jobs belonging to test-key-123
        Assert.DoesNotContain(items, item =>
            item.TryGetProperty("pullRequestId", out var pr) && pr.GetInt32() == 800);
    }

    public sealed class ListReviewsFactory : WebApplicationFactory<Program>
    {
        private IJobRepository? _jobRepo;

        public ListReviewsFactory()
        {
            Environment.SetEnvironmentVariable("MEISTER_CLIENT_KEYS", "test-key-123,test-key-456");
            Environment.SetEnvironmentVariable("AI_ENDPOINT", "https://fake-ai.openai.azure.com/");
            Environment.SetEnvironmentVariable("AI_DEPLOYMENT", "gpt-4o");
        }

        private static void ReplaceService<T>(IServiceCollection services, T implementation) where T : class
        {
            var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(T));
            if (descriptor != null)
            {
                services.Remove(descriptor);
            }

            services.AddSingleton(implementation);
        }

        public void InsertJob(string clientKey, int prId)
        {
            var repo = this._jobRepo ?? throw new InvalidOperationException("Factory not initialized");
            var job = new ReviewJob(
                Guid.NewGuid(),
                clientKey,
                "https://dev.azure.com/org",
                "proj",
                "repo",
                prId,
                1);
            repo.Add(job);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                var adoValidator = Substitute.For<IAdoTokenValidator>();
                adoValidator.IsValidAsync("valid-ado-token", Arg.Any<CancellationToken>()).Returns(true);
                adoValidator.IsValidAsync(Arg.Is<string>(s => s != "valid-ado-token"), Arg.Any<CancellationToken>()).Returns(false);
                adoValidator.IsValidAsync(null!, Arg.Any<CancellationToken>()).Returns(false);

                ReplaceService(services, adoValidator);
                ReplaceService(services, Substitute.For<IPullRequestFetcher>());
                ReplaceService(services, Substitute.For<IAdoCommentPoster>());
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = base.CreateHost(builder);
            this._jobRepo = host.Services.GetRequiredService<IJobRepository>();
            return host;
        }
    }
}
