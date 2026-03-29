using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.ValueObjects;
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

    // T050 — PR context is populated when IPullRequestFetcher succeeds

    [Fact]
    public async Task PostReviews_PrFetcherReturnsData_JobReceivesPrContextViaInMemoryRepo()
    {
        // This test uses a factory where IPullRequestFetcher is replaced with a stub that
        // returns a PullRequest with known context fields. We verify that the job stored in
        // the InMemory IJobRepository has those fields set after the POST.
        await using var factory = new ReviewsApiPrContextFactory();
        var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/reviews");
        request.Headers.Add("X-Client-Key", "test-key-123");
        request.Headers.Add("X-Ado-Token", "valid-ado-token");
        request.Content = JsonContent.Create(new
        {
            organizationUrl = "https://dev.azure.com/myorg",
            projectId = "my-project",
            repositoryId = "my-repo",
            pullRequestId = 77,
            iterationId = 1,
        });

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var jobId = body.RootElement.GetProperty("jobId").GetGuid();

        // Retrieve the job from the in-memory repository and verify PR context was set.
        using var scope = factory.Services.CreateScope();
        var jobRepo = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = jobRepo.GetById(jobId);
        Assert.NotNull(job);
        Assert.Equal("Add feature X", job.PrTitle);
        Assert.Equal("main", job.PrTargetBranch);
    }

    [Fact]
    public async Task PostReviews_PrFetcherThrows_JobStillCreatedWith202()
    {
        // T050 — PR fetch failure is non-blocking: 202 is returned even if IPullRequestFetcher throws.
        await using var factory = new ReviewsApiPrFetcherThrowsFactory();
        var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/reviews");
        request.Headers.Add("X-Client-Key", "test-key-123");
        request.Headers.Add("X-Ado-Token", "valid-ado-token");
        request.Content = JsonContent.Create(new
        {
            organizationUrl = "https://dev.azure.com/myorg",
            projectId = "proj-throws",
            repositoryId = "repo-throws",
            pullRequestId = 88,
            iterationId = 1,
        });

        var response = await client.SendAsync(request);

        // Job must be created regardless of the fetcher failure.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.TryGetProperty("jobId", out _));
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
                adoValidator.IsValidAsync("valid-ado-token", Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns(true);
                adoValidator.IsValidAsync(Arg.Is<string>(s => s != "valid-ado-token"), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .Returns(false);

                ReplaceService(services, adoValidator);
                ReplaceService(services, Substitute.For<IPullRequestFetcher>());
                ReplaceService(services, Substitute.For<IAdoCommentPoster>());
            });
        }
    }
}

/// <summary>
///     T050: factory where <see cref="IPullRequestFetcher" /> returns known PR context fields.
/// </summary>
internal sealed class ReviewsApiPrContextFactory : WebApplicationFactory<Program>
{
    public ReviewsApiPrContextFactory()
    {
        Environment.SetEnvironmentVariable("MEISTER_CLIENT_KEYS", "test-key-123");
        Environment.SetEnvironmentVariable("AI_ENDPOINT", "https://fake-ai.openai.azure.com/");
        Environment.SetEnvironmentVariable("AI_DEPLOYMENT", "gpt-4o");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            // ADO validator: "valid-ado-token" is accepted.
            var adoValidator = Substitute.For<IAdoTokenValidator>();
            adoValidator.IsValidAsync("valid-ado-token", Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(true);
            adoValidator.IsValidAsync(Arg.Is<string>(s => s != "valid-ado-token"), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(false);

            // PR fetcher that returns PR context for any call.
            var prFetcher = Substitute.For<IPullRequestFetcher>();
            prFetcher.FetchAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                    Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new PullRequest(
                    "https://dev.azure.com/myorg",
                    "my-project",
                    "my-repo",
                    "my-repo",
                    77,
                    1,
                    "Add feature X",
                    null,
                    "feature/add-x",
                    "main",
                    [])));

            Remove<IAdoTokenValidator>(services);
            Remove<IPullRequestFetcher>(services);
            Remove<IAdoCommentPoster>(services);
            services.AddSingleton(adoValidator);
            services.AddSingleton(prFetcher);
            services.AddSingleton(Substitute.For<IAdoCommentPoster>());
        });
    }

    private static void Remove<T>(IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(T));
        if (descriptor is not null)
        {
            services.Remove(descriptor);
        }
    }
}

/// <summary>
///     T050: factory where <see cref="IPullRequestFetcher" /> always throws to verify job creation is non-blocking.
/// </summary>
internal sealed class ReviewsApiPrFetcherThrowsFactory : WebApplicationFactory<Program>
{
    public ReviewsApiPrFetcherThrowsFactory()
    {
        Environment.SetEnvironmentVariable("MEISTER_CLIENT_KEYS", "test-key-123");
        Environment.SetEnvironmentVariable("AI_ENDPOINT", "https://fake-ai.openai.azure.com/");
        Environment.SetEnvironmentVariable("AI_DEPLOYMENT", "gpt-4o");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            var adoValidator = Substitute.For<IAdoTokenValidator>();
            adoValidator.IsValidAsync("valid-ado-token", Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(true);
            adoValidator.IsValidAsync(Arg.Is<string>(s => s != "valid-ado-token"), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(false);

            var prFetcher = Substitute.For<IPullRequestFetcher>();
            prFetcher.FetchAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
                    Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
                .Returns<Task<PullRequest>>(_ => throw new InvalidOperationException("ADO unavailable"));

            Remove<IAdoTokenValidator>(services);
            Remove<IPullRequestFetcher>(services);
            Remove<IAdoCommentPoster>(services);
            services.AddSingleton(adoValidator);
            services.AddSingleton(prFetcher);
            services.AddSingleton(Substitute.For<IAdoCommentPoster>());
        });
    }

    private static void Remove<T>(IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(T));
        if (descriptor is not null)
        {
            services.Remove(descriptor);
        }
    }
}
