// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterProPR.Api.Tests.Controllers.ProCursor;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Features.ProCursor;

public sealed class ProCursorModuleIntegrationTests(ProCursorKnowledgeSourcesControllerTests.ProCursorApiFactory factory)
    : IClassFixture<ProCursorKnowledgeSourcesControllerTests.ProCursorApiFactory>, IAsyncLifetime
{
    public Task InitializeAsync()
    {
        return factory.ResetAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CreateSource_ThenListSources_ReturnsPersistedFeatureOwnedSource()
    {
        var http = factory.CreateClient();
        using var createRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/admin/clients/{factory.ClientId}/procursor/sources");
        createRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());
        createRequest.Content = JsonContent.Create(
            new
            {
                displayName = "Knowledge Repo",
                sourceKind = "repository",
                providerScopePath = "https://dev.azure.com/test-org",
                providerProjectKey = "project-a",
                repositoryId = "repo-a",
                defaultBranch = "main",
                symbolMode = "auto",
                trackedBranches = new[]
                {
                    new { branchName = "main", refreshTriggerMode = "branchUpdate", miniIndexEnabled = true },
                },
            });

        var createResponse = await http.SendAsync(createRequest);

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        using var listRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/clients/{factory.ClientId}/procursor/sources");
        listRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());

        var listResponse = await http.SendAsync(listRequest);

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var items = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync())
            .RootElement.EnumerateArray()
            .ToList();
        Assert.Single(items);
        Assert.Equal("Knowledge Repo", items[0].GetProperty("displayName").GetString());
    }

    [Fact]
    public async Task Healthz_WhenRemoteProCursorIsConfigured_ReportsRemoteDependencyFailure()
    {
        await using var remoteFactory = new RemoteProCursorHealthApiFactory();
        var client = remoteFactory.CreateClient();

        var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entries = document.RootElement.GetProperty("entries");
        var remoteEntry = entries.GetProperty("procursor-remote");

        Assert.Equal("Unhealthy", remoteEntry.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Healthz_WhenProCursorIsDisabled_OmitsRemoteDependencyCheck()
    {
        await using var disabledFactory = new DisabledProCursorHealthApiFactory();
        var client = disabledFactory.CreateClient();

        var response = await client.GetAsync("/healthz");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var entries = document.RootElement.GetProperty("entries");

        Assert.False(entries.TryGetProperty("procursor-remote", out _));
    }

    private sealed class RemoteProCursorHealthApiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
            builder.UseSetting("MEISTER_JWT_SECRET", "test-procursor-remote-health-jwt-secret");
            builder.UseSetting("PROCURSOR_REMOTE_MODE", "proprManagedRemote");
            builder.UseSetting("PROCURSOR_SERVICE_BASE_URL", "http://127.0.0.1:1");
            builder.UseSetting("PROCURSOR_SHARED_KEY", "test-shared-key");
            builder.UseSetting("AI_ENDPOINT", "https://fake-ai.openai.azure.com/");
            builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");

            builder.ConfigureServices(services =>
            {
                ReplaceService(services, Substitute.For<IPullRequestFetcher>());
                ReplaceService(services, Substitute.For<IAdoCommentPoster>());
                ReplaceService(services, Substitute.For<IAssignedReviewDiscoveryService>());
                services.AddSingleton(Substitute.For<IJobRepository>());
                services.AddSingleton(Substitute.For<IClientRegistry>());
            });
        }

        private static void ReplaceService<T>(IServiceCollection services, T implementation) where T : class
        {
            var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(T));
            if (descriptor is not null)
            {
                services.Remove(descriptor);
            }

            services.AddSingleton(implementation);
        }
    }

    private sealed class DisabledProCursorHealthApiFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
            builder.UseSetting("MEISTER_JWT_SECRET", "test-procursor-disabled-health-jwt-secret");
            builder.UseSetting("PROCURSOR_REMOTE_MODE", "disabled");
            builder.UseSetting("AI_ENDPOINT", "https://fake-ai.openai.azure.com/");
            builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");

            builder.ConfigureServices(services =>
            {
                ReplaceService(services, Substitute.For<IPullRequestFetcher>());
                ReplaceService(services, Substitute.For<IAdoCommentPoster>());
                ReplaceService(services, Substitute.For<IAssignedReviewDiscoveryService>());
                services.AddSingleton(Substitute.For<IJobRepository>());
                services.AddSingleton(Substitute.For<IClientRegistry>());
            });
        }

        private static void ReplaceService<T>(IServiceCollection services, T implementation) where T : class
        {
            var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(T));
            if (descriptor is not null)
            {
                services.Remove(descriptor);
            }

            services.AddSingleton(implementation);
        }
    }
}
