using System.Net;
using System.Net.Http.Json;
using MeisterProPR.Application.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Middleware;

public class ClientKeyMiddlewareTests(ClientKeyMiddlewareTests.TestWebApplicationFactory factory)
    : IClassFixture<ClientKeyMiddlewareTests.TestWebApplicationFactory>
{
    [Fact]
    public async Task HealthzEndpoint_DoesNotRequireClientKey_Returns200Or503()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        // No X-Client-Key header

        var response = await client.SendAsync(request);

        // Should be 200 (Healthy) or 503 (Unhealthy) but NOT 401
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(
            response.StatusCode == HttpStatusCode.OK ||
            response.StatusCode == HttpStatusCode.ServiceUnavailable,
            $"Expected 200 or 503 but got {response.StatusCode}");
    }

    [Fact]
    public async Task RequestWithInvalidClientKey_Returns401()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/reviews");
        request.Headers.Add("X-Client-Key", "invalid-key");
        request.Content = JsonContent.Create(
            new
            {
                organizationUrl = "https://dev.azure.com/org",
                projectId = "proj",
                repositoryId = "repo",
                pullRequestId = 1,
                iterationId = 1,
            });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RequestWithNoClientKeyHeader_Returns401()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/reviews");
        request.Content = JsonContent.Create(
            new
            {
                organizationUrl = "https://dev.azure.com/org",
                projectId = "proj",
                repositoryId = "repo",
                pullRequestId = 1,
                iterationId = 1,
            });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RequestWithValidClientKey_PassesThrough()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/reviews");
        request.Headers.Add("X-Client-Key", "test-key-123");
        request.Headers.Add("X-Ado-Token", "fake-ado-token");
        request.Content = JsonContent.Create(
            new
            {
                organizationUrl = "https://dev.azure.com/org",
                projectId = "proj",
                repositoryId = "repo",
                pullRequestId = 1,
                iterationId = 1,
            });

        var response = await client.SendAsync(request);

        // Should NOT be 401 (could be 200/202/any other status - it passed middleware)
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public sealed class TestWebApplicationFactory : WebApplicationFactory<Program>
    {
        public TestWebApplicationFactory()
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
                // Replace real infrastructure with stubs
                var adoValidator = Substitute.For<IAdoTokenValidator>();
                adoValidator.IsValidAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(true);

                var prFetcher = Substitute.For<IPullRequestFetcher>();
                var commentPoster = Substitute.For<IAdoCommentPoster>();

                // Remove and replace real registrations
                ReplaceService(services, adoValidator);
                ReplaceService(services, prFetcher);
                ReplaceService(services, commentPoster);
            });
        }
    }
}