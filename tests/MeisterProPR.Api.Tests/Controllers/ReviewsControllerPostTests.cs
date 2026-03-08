using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterProPR.Application.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Controllers;

public class ReviewsControllerPostTests(ReviewsControllerPostTests.ReviewsApiFactory factory) : IClassFixture<ReviewsControllerPostTests.ReviewsApiFactory>
{
    private static HttpRequestMessage CreateValidRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/reviews");
        request.Headers.Add("X-Client-Key", "test-key-123");
        request.Headers.Add("X-Ado-Token", "valid-ado-token");
        request.Content = JsonContent.Create(
            new
            {
                organizationUrl = "https://dev.azure.com/myorg",
                projectId = "my-project",
                repositoryId = "my-repo",
                pullRequestId = 42,
                iterationId = 1,
            });
        return request;
    }

    [Fact]
    public async Task PostReviews_InvalidAdoToken_Returns401()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/reviews");
        request.Headers.Add("X-Client-Key", "test-key-123");
        request.Headers.Add("X-Ado-Token", "invalid-token"); // factory returns false for this
        request.Content = JsonContent.Create(
            new
            {
                organizationUrl = "https://dev.azure.com/myorg",
                projectId = "proj",
                repositoryId = "repo",
                pullRequestId = 1,
                iterationId = 1,
            });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostReviews_MissingClientKey_Returns401()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/reviews");
        // No X-Client-Key header
        request.Headers.Add("X-Ado-Token", "valid-ado-token");
        request.Content = JsonContent.Create(
            new
            {
                organizationUrl = "https://dev.azure.com/myorg",
                projectId = "proj",
                repositoryId = "repo",
                pullRequestId = 1,
                iterationId = 1,
            });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostReviews_SamePrIterationSubmittedTwice_ReturnsSameJobId()
    {
        var client = factory.CreateClient();

        // First submission
        using var request1 = new HttpRequestMessage(HttpMethod.Post, "/reviews");
        request1.Headers.Add("X-Client-Key", "test-key-123");
        request1.Headers.Add("X-Ado-Token", "valid-ado-token");
        request1.Content = JsonContent.Create(
            new
            {
                organizationUrl = "https://dev.azure.com/myorg",
                projectId = "proj-idempotent",
                repositoryId = "repo-idempotent",
                pullRequestId = 99,
                iterationId = 1,
            });

        var response1 = await client.SendAsync(request1);
        Assert.Equal(HttpStatusCode.Accepted, response1.StatusCode);
        var body1 = JsonDocument.Parse(await response1.Content.ReadAsStringAsync());
        var jobId1 = body1.RootElement.GetProperty("jobId").GetString();

        // Second submission - same PR
        using var request2 = new HttpRequestMessage(HttpMethod.Post, "/reviews");
        request2.Headers.Add("X-Client-Key", "test-key-123");
        request2.Headers.Add("X-Ado-Token", "valid-ado-token");
        request2.Content = JsonContent.Create(
            new
            {
                organizationUrl = "https://dev.azure.com/myorg",
                projectId = "proj-idempotent",
                repositoryId = "repo-idempotent",
                pullRequestId = 99,
                iterationId = 1,
            });

        var response2 = await client.SendAsync(request2);
        Assert.Equal(HttpStatusCode.Accepted, response2.StatusCode);
        var body2 = JsonDocument.Parse(await response2.Content.ReadAsStringAsync());
        var jobId2 = body2.RootElement.GetProperty("jobId").GetString();

        // Both should return the same jobId (idempotency)
        Assert.Equal(jobId1, jobId2);
    }

    [Fact]
    public async Task PostReviews_ValidRequest_Returns202WithJobId()
    {
        var client = factory.CreateClient();
        using var request = CreateValidRequest();

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.NotEmpty(body);

        // Verify it's valid JSON with a jobId field
        var json = JsonDocument.Parse(body);
        Assert.True(json.RootElement.TryGetProperty("jobId", out var jobIdElement));
        Assert.True(Guid.TryParse(jobIdElement.GetString(), out var jobId));
        Assert.NotEqual(Guid.Empty, jobId);
    }

    public sealed class ReviewsApiFactory : WebApplicationFactory<Program>
    {
        public ReviewsApiFactory()
        {
            Environment.SetEnvironmentVariable("MEISTER_CLIENT_KEYS", "test-key-123");
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

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                var adoValidator = Substitute.For<IAdoTokenValidator>();
                // "valid-ado-token" returns true; everything else returns false
                adoValidator.IsValidAsync("valid-ado-token", Arg.Any<CancellationToken>())
                    .Returns(true);
                adoValidator.IsValidAsync(Arg.Is<string>(s => s != "valid-ado-token"), Arg.Any<CancellationToken>())
                    .Returns(false);

                ReplaceService(services, adoValidator);
                ReplaceService(services, Substitute.For<IPullRequestFetcher>());
                ReplaceService(services, Substitute.For<IAdoCommentPoster>());
            });
        }
    }
}