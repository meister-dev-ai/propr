using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Controllers;

public class ReviewsControllerGetTests(ReviewsControllerGetTests.GetReviewsFactory factory) : IClassFixture<ReviewsControllerGetTests.GetReviewsFactory>
{
    private static HttpRequestMessage CreateGetRequest(string jobId)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/reviews/{jobId}");
        request.Headers.Add("X-Client-Key", "test-key-123");
        request.Headers.Add("X-Ado-Token", "valid-ado-token");
        return request;
    }

    [Fact]
    public async Task GetReview_CompletedJob_Returns200WithResult()
    {
        // Use the factory's job repository directly to insert a completed job
        var client = factory.CreateClient();
        var jobId = factory.InsertCompletedJob();

        using var request = CreateGetRequest(jobId.ToString());
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(body.RootElement.TryGetProperty("status", out var statusEl));
        Assert.Equal("completed", statusEl.GetString());

        Assert.True(body.RootElement.TryGetProperty("result", out var resultEl));
        Assert.True(resultEl.TryGetProperty("summary", out _));
    }

    [Fact]
    public async Task GetReview_FailedJob_Returns200WithError()
    {
        var client = factory.CreateClient();
        var jobId = factory.InsertFailedJob();

        using var request = CreateGetRequest(jobId.ToString());
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(body.RootElement.TryGetProperty("status", out var statusEl));
        Assert.Equal("failed", statusEl.GetString());

        Assert.True(body.RootElement.TryGetProperty("error", out var errorEl));
        Assert.NotNull(errorEl.GetString());
    }

    [Fact]
    public async Task GetReview_NewJob_Returns200WithPendingStatus()
    {
        // First create a job
        var client = factory.CreateClient();
        using var postRequest = new HttpRequestMessage(HttpMethod.Post, "/reviews");
        postRequest.Headers.Add("X-Client-Key", "test-key-123");
        postRequest.Headers.Add("X-Ado-Token", "valid-ado-token");
        postRequest.Content = JsonContent.Create(
            new
            {
                organizationUrl = "https://dev.azure.com/org",
                projectId = "proj-get-test",
                repositoryId = "repo-get-test",
                pullRequestId = 10,
                iterationId = 1,
            });

        var postResponse = await client.SendAsync(postRequest);
        Assert.Equal(HttpStatusCode.Accepted, postResponse.StatusCode);
        var postBody = JsonDocument.Parse(await postResponse.Content.ReadAsStringAsync());
        var jobId = postBody.RootElement.GetProperty("jobId").GetString()!;

        // Now get the job
        using var getRequest = CreateGetRequest(jobId);
        var getResponse = await client.SendAsync(getRequest);

        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var getBody = JsonDocument.Parse(await getResponse.Content.ReadAsStringAsync());

        Assert.True(getBody.RootElement.TryGetProperty("status", out var statusElement));
        Assert.Equal("pending", statusElement.GetString());
        Assert.True(getBody.RootElement.TryGetProperty("jobId", out _));
    }

    [Fact]
    public async Task GetReview_NoAdoToken_Returns401()
    {
        var client = factory.CreateClient();
        var unknownId = Guid.NewGuid().ToString();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/reviews/{unknownId}");
        request.Headers.Add("X-Client-Key", "test-key-123");
        // No X-Ado-Token

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetReview_UnknownJobId_Returns404()
    {
        var client = factory.CreateClient();
        var unknownId = Guid.NewGuid().ToString();
        using var request = CreateGetRequest(unknownId);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public sealed class GetReviewsFactory : WebApplicationFactory<Program>
    {
        private IJobRepository? _jobRepo;

        public GetReviewsFactory()
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

        public Guid InsertCompletedJob()
        {
            var repo = this._jobRepo ?? throw new InvalidOperationException("Factory not initialized");
            var job = new ReviewJob(
                Guid.NewGuid(),
                "test-key-123",
                "https://dev.azure.com/org",
                "proj",
                "repo",
                200,
                1);
            repo.Add(job);
            repo.SetResult(job.Id, new ReviewResult("AI completed", new List<ReviewComment>().AsReadOnly()));
            return job.Id;
        }

        public Guid InsertFailedJob()
        {
            var repo = this._jobRepo ?? throw new InvalidOperationException("Factory not initialized");
            var job = new ReviewJob(
                Guid.NewGuid(),
                "test-key-123",
                "https://dev.azure.com/org",
                "proj",
                "repo",
                300,
                1);
            repo.Add(job);
            repo.SetFailed(job.Id, "Something went wrong");
            return job.Id;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                var adoValidator = Substitute.For<IAdoTokenValidator>();
                adoValidator.IsValidAsync("valid-ado-token", Arg.Any<CancellationToken>())
                    .Returns(true);
                adoValidator.IsValidAsync(Arg.Is<string>(s => s != "valid-ado-token"), Arg.Any<CancellationToken>())
                    .Returns(false);
                // empty/null token (missing header) also returns false
                adoValidator.IsValidAsync(null!, Arg.Any<CancellationToken>())
                    .Returns(false);

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