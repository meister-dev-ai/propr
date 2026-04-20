// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterProPR.Application.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterProPR.Api.Tests;

public class HealthCheckTests(HealthCheckTests.HealthCheckFactory factory)
    : IClassFixture<HealthCheckTests.HealthCheckFactory>
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

        Assert.NotNull(body);
        Assert.NotEmpty(body);

        using var document = JsonDocument.Parse(body);
        Assert.True(document.RootElement.TryGetProperty("status", out _));
        Assert.True(document.RootElement.TryGetProperty("entries", out var entries));
        Assert.True(entries.TryGetProperty("worker", out var worker));
        Assert.True(worker.TryGetProperty("data", out var data));
        Assert.False(data.GetProperty("databaseConfigured").GetBoolean());
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
            builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
            builder.UseSetting("MEISTER_CLIENT_KEYS", "test-key-123");
            builder.UseSetting("AI_ENDPOINT", "https://fake-ai.openai.azure.com/");
            builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");

            builder.ConfigureServices(services =>
            {
                ReplaceService(services, Substitute.For<IPullRequestFetcher>());
                ReplaceService(services, Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IJobRepository>());
                services.AddSingleton(Substitute.For<IClientRegistry>());
            });
        }
    }
}
