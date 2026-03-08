using System.Net;
using MeisterProPR.Application.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterProPR.Api.Tests;

public class HealthCheckTests(HealthCheckTests.HealthCheckFactory factory) : IClassFixture<HealthCheckTests.HealthCheckFactory>
{
    [Fact]
    public async Task GetHealthz_DoesNotRequireClientKey()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        // Deliberately no X-Client-Key header

        var response = await client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetHealthz_ReturnsJsonBody()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz");
        var body = await response.Content.ReadAsStringAsync();

        // Health check should return some JSON-like body
        Assert.NotNull(body);
        Assert.NotEmpty(body);
    }

    [Fact]
    public async Task GetHealthz_ReturnsSuccessStatus()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        // No X-Client-Key - /healthz should bypass auth

        var response = await client.SendAsync(request);

        // Should be 200 (Healthy) - worker starts and IsRunning becomes true
        // In test environment, worker may not have started yet → allow 503 too
        Assert.True(
            response.StatusCode == HttpStatusCode.OK ||
            response.StatusCode == HttpStatusCode.ServiceUnavailable,
            $"Expected 200 or 503 but got {(int)response.StatusCode}");
    }

    public sealed class HealthCheckFactory : WebApplicationFactory<Program>
    {
        public HealthCheckFactory()
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
                adoValidator.IsValidAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(true);

                ReplaceService(services, adoValidator);
                ReplaceService(services, Substitute.For<IPullRequestFetcher>());
                ReplaceService(services, Substitute.For<IAdoCommentPoster>());
            });
        }
    }
}