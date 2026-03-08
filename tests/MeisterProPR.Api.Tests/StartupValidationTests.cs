using MeisterProPR.Application.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterProPR.Api.Tests;

public class StartupValidationTests
{
    [Fact]
    public void Startup_MissingAiDeployment_ThrowsInvalidOperationException()
    {
        var originalEndpoint = Environment.GetEnvironmentVariable("AI_ENDPOINT");
        var original = Environment.GetEnvironmentVariable("AI_DEPLOYMENT");
        var originalKeys = Environment.GetEnvironmentVariable("MEISTER_CLIENT_KEYS");

        Environment.SetEnvironmentVariable("AI_ENDPOINT", "https://fake.openai.azure.com/");
        Environment.SetEnvironmentVariable("AI_DEPLOYMENT", null);
        Environment.SetEnvironmentVariable("MEISTER_CLIENT_KEYS", "test-key");

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                using var factory = new MissingAiDeploymentFactory();
                _ = factory.CreateClient();
            });

            Assert.Contains("AI_DEPLOYMENT", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AI_ENDPOINT", originalEndpoint);
            Environment.SetEnvironmentVariable("AI_DEPLOYMENT", original);
            Environment.SetEnvironmentVariable("MEISTER_CLIENT_KEYS", originalKeys);
        }
    }

    [Fact]
    public void Startup_MissingAiEndpoint_ThrowsInvalidOperationException()
    {
        // Store and clear the env var
        var original = Environment.GetEnvironmentVariable("AI_ENDPOINT");
        Environment.SetEnvironmentVariable("AI_ENDPOINT", null);

        // Ensure other required vars are set
        var originalDeployment = Environment.GetEnvironmentVariable("AI_DEPLOYMENT");
        var originalKeys = Environment.GetEnvironmentVariable("MEISTER_CLIENT_KEYS");
        Environment.SetEnvironmentVariable("AI_DEPLOYMENT", "gpt-4o");
        Environment.SetEnvironmentVariable("MEISTER_CLIENT_KEYS", "test-key");

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                using var factory = new MissingAiEndpointFactory();
                _ = factory.CreateClient(); // triggers startup
            });

            Assert.Contains("AI_ENDPOINT", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AI_ENDPOINT", original);
            Environment.SetEnvironmentVariable("AI_DEPLOYMENT", originalDeployment);
            Environment.SetEnvironmentVariable("MEISTER_CLIENT_KEYS", originalKeys);
        }
    }

    [Fact]
    public void Startup_MissingClientKeys_ThrowsInvalidOperationException()
    {
        var originalEndpoint = Environment.GetEnvironmentVariable("AI_ENDPOINT");
        var originalDeployment = Environment.GetEnvironmentVariable("AI_DEPLOYMENT");
        var original = Environment.GetEnvironmentVariable("MEISTER_CLIENT_KEYS");

        Environment.SetEnvironmentVariable("AI_ENDPOINT", "https://fake.openai.azure.com/");
        Environment.SetEnvironmentVariable("AI_DEPLOYMENT", "gpt-4o");
        Environment.SetEnvironmentVariable("MEISTER_CLIENT_KEYS", null);

        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
            {
                using var factory = new MissingClientKeysFactory();
                _ = factory.CreateClient();
            });

            Assert.Contains("MEISTER_CLIENT_KEYS", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AI_ENDPOINT", originalEndpoint);
            Environment.SetEnvironmentVariable("AI_DEPLOYMENT", originalDeployment);
            Environment.SetEnvironmentVariable("MEISTER_CLIENT_KEYS", original);
        }
    }

    // These factory classes override configuration to simulate missing env vars
    private sealed class MissingAiEndpointFactory : WebApplicationFactory<Program>
    {
        private static void ReplaceWithStubs(IServiceCollection services)
        {
            services.AddSingleton(Substitute.For<IAdoTokenValidator>());
            services.AddSingleton(Substitute.For<IPullRequestFetcher>());
            services.AddSingleton(Substitute.For<IAdoCommentPoster>());
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AI_ENDPOINT", ""); // force empty
            builder.ConfigureServices(services => { ReplaceWithStubs(services); });
        }
    }

    private sealed class MissingAiDeploymentFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("AI_DEPLOYMENT", "");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton(Substitute.For<IAdoTokenValidator>());
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
            });
        }
    }

    private sealed class MissingClientKeysFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("MEISTER_CLIENT_KEYS", "");
            builder.ConfigureServices(services =>
            {
                services.AddSingleton(Substitute.For<IAdoTokenValidator>());
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
            });
        }
    }
}