// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Application.Features.Crawling.Configuration;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using MeisterDev.ProPR.TestSupport;
using MeisterDev.ProPR.ProCursor.Contracts.Sources;
using MeisterDev.ProPR.Api.Features.Crawling.Contracts.AzureDevOps;

namespace MeisterDev.ProPR.Api.Tests.Controllers;

/// <summary>Integration tests for <see cref="MeisterDev.ProPR.Api.Controllers.AdminCrawlConfigsController" />.</summary>
public sealed class AdminCrawlConfigsControllerTests(AdminCrawlConfigsControllerTests.AdminCrawlConfigsApiFactory factory)
    : IClassFixture<AdminCrawlConfigsControllerTests.AdminCrawlConfigsApiFactory>
{
    private const string ValidAdminKey = "admin-key-min-16-chars-ok";
    private readonly bool _capabilityDefaultsInitialized = InitializeCapabilityDefaults(factory);

    private static bool InitializeCapabilityDefaults(AdminCrawlConfigsApiFactory factory)
    {
        factory.SetCrawlConfigsCapabilityAvailability(true);
        return true;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task CreateConfiguration_UnknownProviderWithoutPathPreserves400(string? path)
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/crawl-configurations");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateUserToken(Guid.NewGuid(), "Admin"));
        request.Content = JsonContent.Create(
            new
            {
                clientId = factory.TestClientId, provider = 999, organizationScopeId = Guid.NewGuid(),
                providerScopePath = path, providerProjectKey = "project", crawlIntervalSeconds = 60,
                enabledEvents = new[] { "pullRequestCreated" },
            });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("errors");
        Assert.Equal("Select a connection and scope, or supply an explicit provider and its manual scope coordinates.", errors.GetProperty("")[0].GetString());
        Assert.False(errors.TryGetProperty("Provider", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateCrawlConfiguration_UsesSuppliedSelectionForScopeAndFilters(bool rejectScope)
    {
        var selection = Substitute.For<IReviewConfigurationSelectionService>();
        selection.HasScopeSelection(Arg.Any<ScmProvider>(), Arg.Any<Guid?>(), Arg.Any<string?>()).Returns(true);
        selection.ResolveScopeAsync(
            factory.TestClientId, Arg.Any<ScmProvider>(), Arg.Any<Guid?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>(), "crawl").Returns(_ => rejectScope
            ? Task.FromException<(Guid?, string)>(new InvalidOperationException("selection scope refusal"))
            : Task.FromResult<(Guid?, string)>((null, "https://dev.azure.com/selection")));
        selection.ResolveFiltersAsync(
                factory.TestClientId, Arg.Any<ScmProvider>(), Arg.Any<Guid?>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<CrawlRepoFilterDto>?>(), Arg.Any<CancellationToken>(), "crawl")
            .Returns(Task.FromException<IReadOnlyList<CrawlRepoFilterDto>>(new InvalidOperationException("selection filter refusal")));
        using var selectedFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton(selection)));
        var client = selectedFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/crawl-configurations");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateUserToken(Guid.NewGuid(), "Admin"));
        request.Content = JsonContent.Create(
            new
            {
                clientId = factory.TestClientId, provider = "azureDevOps", providerScopePath = "https://dev.azure.com/selection",
                providerProjectKey = "selection-project", crawlIntervalSeconds = 60,
            });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(rejectScope ? "selection scope refusal" : "selection filter refusal", await response.Content.ReadAsStringAsync());
        await selection.Received(1).ResolveScopeAsync(
            factory.TestClientId, Arg.Any<ScmProvider>(), null,
            "https://dev.azure.com/selection", Arg.Any<CancellationToken>(), "crawl");
        await selection.Received(rejectScope ? 0 : 1).ResolveFiltersAsync(
            factory.TestClientId, Arg.Any<ScmProvider>(), null,
            "selection-project", null, Arg.Any<CancellationToken>(), "crawl");
    }

    // --- GET /admin/crawl-configurations ---

    [Fact]
    public async Task GetCrawlConfigs_WithAdminKey_Returns200WithAllConfigs()
    {
        // Admin using X-Admin-Key → should get all configs
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/crawl-configurations");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(Guid.NewGuid(), "Admin"));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var items = JsonDocument.Parse(body).RootElement;
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
    }

    [Fact]
    public async Task GetCrawlConfigs_WithAdminKey_ReturnsPausedConfigsToo()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/crawl-configurations");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(Guid.NewGuid(), "Admin"));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var items = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement
            .EnumerateArray()
            .ToList();

        Assert.Contains(items, item => item.GetProperty("isActive").GetBoolean() is false);
    }

    [Fact]
    public async Task GetCrawlConfigs_NoCredentials_Returns401()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/crawl-configurations");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetCrawlConfigs_WithUserJwt_Returns200WithScopedConfigs()
    {
        // User with 2 client assignments → should get only scoped configs
        var userId = factory.TestUserId;
        var token = factory.GenerateUserToken(userId);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/crawl-configurations");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var items = JsonDocument.Parse(body).RootElement;
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
    }

    [Fact]
    public async Task GetCrawlConfigs_WhenCapabilityUnavailable_Returns409PremiumUnavailable()
    {
        factory.SetCrawlConfigsCapabilityAvailability(false, "Crawl configs requires a premium license.");

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/crawl-configurations");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(Guid.NewGuid(), "Admin"));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("premium_feature_unavailable", body.GetProperty("error").GetString());
        Assert.Equal(PremiumCapabilityKey.CrawlConfigs, body.GetProperty("feature").GetString());
    }

    // --- POST /admin/crawl-configurations ---

    [Fact]
    public async Task PostCrawlConfig_AdminWithValidBody_Returns201()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/crawl-configurations");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(Guid.NewGuid(), "Admin"));
        request.Content = JsonContent.Create(
            new
            {
                clientId = factory.TestClientId, provider = "azureDevOps",
                providerScopePath = "https://dev.azure.com/myorg",
                providerProjectKey = "MyProject",
                crawlIntervalSeconds = 60,
            });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task PostCrawlConfig_AdminWithGuidedSelections_Returns201WithGuidedMetadata()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/crawl-configurations");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(Guid.NewGuid(), "Admin"));
        request.Content = JsonContent.Create(
            new
            {
                clientId = factory.TestClientId, provider = "azureDevOps",
                organizationScopeId = factory.GuidedOrganizationScopeId,
                providerProjectKey = "GuidedProject",
                crawlIntervalSeconds = 60,
                repoFilters = new[]
                {
                    new
                    {
                        displayName = "Repository One",
                        canonicalSourceRef = new
                        {
                            provider = "azureDevOps",
                            value = "repo-1",
                        },
                        targetBranchPatterns = new[] { "main" },
                    },
                },
                proCursorSourceScopeMode = "selectedSources",
                proCursorSourceIds = new[] { factory.GuidedProCursorSourceId },
            });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(factory.GuidedOrganizationScopeId, body.GetProperty("organizationScopeId").GetGuid());
        Assert.Equal("https://dev.azure.com/testorg", body.GetProperty("providerScopePath").GetString());
        Assert.Equal("GuidedProject", body.GetProperty("providerProjectKey").GetString());
        var repoFilter = body.GetProperty("repoFilters")[0];
        Assert.Equal("Repository One", repoFilter.GetProperty("displayName").GetString());
        Assert.Equal("azureDevOps", repoFilter.GetProperty("canonicalSourceRef").GetProperty("provider").GetString());
        Assert.Equal("repo-1", repoFilter.GetProperty("canonicalSourceRef").GetProperty("value").GetString());
        Assert.Equal(factory.GuidedProCursorSourceId, body.GetProperty("proCursorSourceIds")[0].GetGuid());
    }

    [Fact]
    public async Task PostCrawlConfig_SelectedSourceOutsideClientScope_Returns409()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/crawl-configurations");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(Guid.NewGuid(), "Admin"));
        request.Content = JsonContent.Create(
            new
            {
                clientId = factory.TestClientId, provider = "azureDevOps",
                organizationScopeId = factory.GuidedOrganizationScopeId,
                providerProjectKey = "GuidedProject",
                crawlIntervalSeconds = 60,
                proCursorSourceScopeMode = "selectedSources",
                proCursorSourceIds = new[] { Guid.NewGuid() },
            });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Contains(
            "no longer eligible",
            body.GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostCrawlConfig_AdminWithGuidedSelections_CompletesWithinQuickstartBudget()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/crawl-configurations");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(Guid.NewGuid(), "Admin"));
        request.Content = JsonContent.Create(
            new
            {
                clientId = factory.TestClientId, provider = "azureDevOps",
                organizationScopeId = factory.GuidedOrganizationScopeId,
                providerProjectKey = "GuidedProject",
                crawlIntervalSeconds = 60,
                repoFilters = new[]
                {
                    new
                    {
                        displayName = "Repository One",
                        canonicalSourceRef = new
                        {
                            provider = "azureDevOps",
                            value = "repo-1",
                        },
                        targetBranchPatterns = new[] { "main" },
                    },
                },
                proCursorSourceScopeMode = "selectedSources",
                proCursorSourceIds = new[] { factory.GuidedProCursorSourceId },
            });

        var stopwatch = Stopwatch.StartNew();
        var response = await client.SendAsync(request);
        stopwatch.Stop();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Expected the guided crawl-configuration create flow to finish within 2 seconds, but it took {stopwatch.Elapsed.TotalMilliseconds:F0} ms.");
    }

    [Fact]
    public async Task PostCrawlConfig_NoCredentials_Returns401()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/crawl-configurations");
        request.Content = JsonContent.Create(
            new
            {
                clientId = factory.TestClientId, provider = "azureDevOps",
                providerScopePath = "https://dev.azure.com/myorg",
                providerProjectKey = "MyProject",
                crawlIntervalSeconds = 60,
            });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostCrawlConfig_IntervalBelowMinimum_Returns400()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/crawl-configurations");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(Guid.NewGuid(), "Admin"));
        request.Content = JsonContent.Create(
            new
            {
                clientId = factory.TestClientId, provider = "azureDevOps",
                providerScopePath = "https://dev.azure.com/myorg",
                providerProjectKey = "MyProject",
                crawlIntervalSeconds = 5, // below minimum of 10
            });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostCrawlConfig_NonAdminForUnownedClient_Returns403()
    {
        var userId = factory.TestUserId;
        var unownedClientId = Guid.NewGuid(); // user does NOT own this client
        var token = factory.GenerateUserToken(userId);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/crawl-configurations");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(
            new
            {
                clientId = unownedClientId, provider = "azureDevOps",
                providerScopePath = "https://dev.azure.com/myorg",
                providerProjectKey = "MyProject",
                crawlIntervalSeconds = 60,
            });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // --- PATCH /admin/crawl-configurations/{configId} ---

    [Fact]
    public async Task PatchCrawlConfig_AdminUpdatesAny_Returns200()
    {
        var configId = factory.TestConfigId;

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/admin/crawl-configurations/{configId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(Guid.NewGuid(), "Admin"));
        request.Content = JsonContent.Create(new { crawlIntervalSeconds = 120 });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task PatchCrawlConfig_NotFound_Returns404()
    {
        var nonExistentId = Guid.NewGuid();

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/admin/crawl-configurations/{nonExistentId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(Guid.NewGuid(), "Admin"));
        request.Content = JsonContent.Create(new { isActive = false });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedCrawlFilters_RetainLegacyCoordinatesWhileCanonicalPatternsChange(bool replaceFilters)
    {
        var repository = factory.Services.GetRequiredService<ICrawlConfigurationRepository>();
        var original = (await repository.GetByIdAsync(factory.TestConfigId))!;
        var legacy = new CrawlRepoFilterDto(Guid.NewGuid(), "Saved Legacy Repository", ["main"]);
        var canonical = new CrawlRepoFilterDto(Guid.NewGuid(), "Native Repository", ["main"], new("azureDevOps", "canonical-bytes"), "Native Repository");
        var existing = original with { RepoFilters = [legacy, canonical], ProviderProjectKey = "Saved Native Project Name" };
        repository.GetByIdAsync(existing.Id, Arg.Any<CancellationToken>()).Returns(existing);
        repository.UpdateWithResultAsync(
                Arg.Any<Guid>(), Arg.Any<int?>(), Arg.Any<bool?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<float?>(), Arg.Any<bool>(),
                Arg.Any<IReadOnlyList<CrawlRepoFilterDto>?>())
            .Returns(CrawlConfigurationUpdateResult.Updated);
        repository.ClearReceivedCalls();
        var connectionId = Guid.NewGuid();
        var selection = Substitute.For<IReviewConfigurationSelectionService>();
        var context = new ConnectionDiscoveryContext(existing.ClientId, connectionId, new(ScmProvider.AzureDevOps, "https://dev.azure.com"));
        selection.GetConnectionContextAsync(existing.ClientId, connectionId, Arg.Any<CancellationToken>()).Returns(context);
        selection.ResolveConnectionFiltersAsync(
                context, "scope", existing.ProviderProjectKey, Arg.Any<IReadOnlyList<CrawlRepoFilterDto>?>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyList<CrawlRepoFilterDto>>(3).Any(filter => filter.CanonicalSourceRef is null)
                ? Task.FromException<IReadOnlyList<CrawlRepoFilterDto>>(new InvalidOperationException("Legacy rows are not new canonical selections."))
                : Task.FromResult<IReadOnlyList<CrawlRepoFilterDto>>(call.ArgAt<IReadOnlyList<CrawlRepoFilterDto>>(3)));
        using var isolated = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ICrawlConfigurationRepository>();
            services.AddSingleton(repository);
            services.AddSingleton(selection);
        }));
        var client = isolated.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/admin/crawl-configurations/{existing.Id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateUserToken(Guid.NewGuid(), "Admin"));
        request.Content = JsonContent.Create(
            new
            {
                crawlIntervalSeconds = 120,
                connectionId = replaceFilters ? (Guid?)connectionId : null,
                scopeKey = replaceFilters ? "scope" : null,
                repoFilters = replaceFilters
                    ? new[]
                    {
                        new CrawlRepoFilterRequest(legacy.RepositoryName, legacy.TargetBranchPatterns, legacy.CanonicalSourceRef, legacy.DisplayName),
                        new CrawlRepoFilterRequest(canonical.RepositoryName, ["release/*"], canonical.CanonicalSourceRef, canonical.DisplayName),
                    }
                    : null,
            });

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
        var filters = repository.ReceivedCalls().Single(call => call.GetMethodInfo().Name == nameof(ICrawlConfigurationRepository.UpdateWithResultAsync))
            .GetArguments()[7] as IReadOnlyList<CrawlRepoFilterDto>;
        if (replaceFilters)
        {
            Assert.Equal(legacy, filters!.Single(filter => filter.RepositoryName == legacy.RepositoryName));
            Assert.Equal(canonical.CanonicalSourceRef, filters.Single(filter => filter.RepositoryName == canonical.RepositoryName).CanonicalSourceRef);
            Assert.Equal("Saved Native Project Name", (await repository.GetByIdAsync(existing.Id))!.ProviderProjectKey);
        }
        else
        {
            Assert.Null(filters);
            await selection.DidNotReceiveWithAnyArgs().GetConnectionContextAsync(default, default, default);
        }
    }

    [Fact]
    public async Task PatchCrawlConfig_IntervalBelowMinimum_Returns400()
    {
        var configId = factory.TestConfigId;

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/admin/crawl-configurations/{configId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(Guid.NewGuid(), "Admin"));
        request.Content = JsonContent.Create(new { crawlIntervalSeconds = 3 }); // below minimum

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PatchCrawlConfig_ConfigurationUpdateRefusedDoesNotPersistReplacementFilters()
    {
        var repository = factory.Services.GetRequiredService<ICrawlConfigurationRepository>();
        repository.ClearReceivedCalls();
        repository.UpdateWithResultAsync(
                Arg.Any<Guid>(), Arg.Any<int?>(), Arg.Any<bool?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(), Arg.Any<float?>(), Arg.Any<bool>())
            .ReturnsForAnyArgs(CrawlConfigurationUpdateResult.NotFound);
        using var isolatedFactory = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ICrawlConfigurationRepository>();
            services.AddSingleton(repository);
        }));
        var client = isolatedFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/admin/crawl-configurations/{factory.TestConfigId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateUserToken(Guid.NewGuid(), "Admin"));
        request.Content = JsonContent.Create(
            new
            {
                isActive = false,
                repoFilters = Array.Empty<object>(),
                proCursorSourceScopeMode = ProCursorSourceScopeMode.AllClientSources,
            });

        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(request)).StatusCode);
        await repository.DidNotReceiveWithAnyArgs().UpdateRepoFiltersAsync(default, default!);
        await repository.DidNotReceiveWithAnyArgs().UpdateSourceScopeAsync(default, default, default!);
    }

    [Fact]
    public async Task PatchCrawlConfig_NonAdminForUnownedClient_Returns403WithoutUpdate()
    {
        var repository = factory.Services.GetRequiredService<ICrawlConfigurationRepository>();
        repository.ClearReceivedCalls();
        using var isolatedFactory = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ICrawlConfigurationRepository>();
            services.AddSingleton(repository);
        }));
        var client = isolatedFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/admin/crawl-configurations/{factory.UnownedConfigId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateUserToken(factory.TestUserId));
        request.Content = JsonContent.Create(new { isActive = true });

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
        await repository.DidNotReceiveWithAnyArgs().UpdateWithResultAsync(default, default, default, default);
    }

    [Theory]
    [InlineData("disabledActivation")]
    [InlineData("removedActivation")]
    [InlineData("removedFilters")]
    public async Task PatchCrawlConfig_CurrentStateConflict_Returns409WithoutSourceScopeWrite(string conflict)
    {
        var repository = factory.Services.GetRequiredService<ICrawlConfigurationRepository>();
        Assert.Equal(ReviewTargetLifecycle.Enabled, (await repository.GetByIdAsync(factory.TestConfigId))!.ReviewTargetLifecycle);
        repository.ClearReceivedCalls();
        repository.UpdateWithResultAsync(
                Arg.Any<Guid>(), Arg.Any<int?>(), Arg.Any<bool?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>(),
                Arg.Any<float?>(), Arg.Any<bool>(), Arg.Any<IReadOnlyList<CrawlRepoFilterDto>?>())
            .ReturnsForAnyArgs(CrawlConfigurationUpdateResult.Conflict);
        using var isolatedFactory = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ICrawlConfigurationRepository>();
            services.AddSingleton(repository);
        }));
        var client = isolatedFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/admin/crawl-configurations/{factory.TestConfigId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateUserToken(Guid.NewGuid(), "Admin"));
        request.Content = JsonContent.Create(
            new
            {
                isActive = conflict == "removedFilters" ? (bool?)null : true,
                repoFilters = conflict == "removedFilters" ? Array.Empty<object>() : null,
                proCursorSourceScopeMode = ProCursorSourceScopeMode.AllClientSources,
            });

        Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(request)).StatusCode);
        await repository.DidNotReceiveWithAnyArgs().UpdateSourceScopeAsync(default, default, default!);
    }

    [Fact]
    public async Task PatchCrawlConfig_GuidedFilterThatNoLongerExists_Returns409()
    {
        var configId = factory.TestConfigId;

        var repository = factory.Services.GetRequiredService<ICrawlConfigurationRepository>();
        var original = await repository.GetByIdAsync(configId);
        using var isolatedFactory = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ICrawlConfigurationRepository>();
            services.AddSingleton(repository);
        }));
        var client = isolatedFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/admin/crawl-configurations/{configId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(Guid.NewGuid(), "Admin"));
        request.Content = JsonContent.Create(
            new
            {
                isActive = !original!.IsActive,
                repoFilters = new[]
                {
                    new
                    {
                        displayName = "Missing Repo",
                        canonicalSourceRef = new
                        {
                            provider = "azureDevOps",
                            value = "repo-missing",
                        },
                        targetBranchPatterns = new[] { "main" },
                    },
                },
            });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Contains(
            "no longer available",
            body.GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original!.IsActive, (await repository.GetByIdAsync(configId))!.IsActive);
    }

    [Fact]
    public async Task PatchCrawlConfig_LegacyFilterWithoutCanonicalRef_Returns200()
    {
        var configId = factory.TestConfigId;

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/admin/crawl-configurations/{configId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(Guid.NewGuid(), "Admin"));
        request.Content = JsonContent.Create(
            new
            {
                repoFilters = new[]
                {
                    new
                    {
                        repositoryName = "Legacy Repo",
                        targetBranchPatterns = new[] { "release/*" },
                    },
                },
            });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var repoFilter = body.GetProperty("repoFilters")[0];
        Assert.Equal("Legacy Repo", repoFilter.GetProperty("repositoryName").GetString());
        Assert.False(
            repoFilter.TryGetProperty("canonicalSourceRef", out var canonicalSourceRef) &&
            canonicalSourceRef.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task PatchCrawlConfig_SelectedSourceOutsideClientScope_Returns409()
    {
        var configId = factory.TestConfigId;

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/admin/crawl-configurations/{configId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(Guid.NewGuid(), "Admin"));
        request.Content = JsonContent.Create(
            new
            {
                proCursorSourceScopeMode = "selectedSources",
                proCursorSourceIds = new[] { Guid.NewGuid() },
            });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Contains(
            "no longer eligible",
            body.GetProperty("error").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PatchCrawlConfig_ExplicitNullReviewTemperature_ClearsStoredOverride()
    {
        var configId = factory.TestConfigId;

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/admin/crawl-configurations/{configId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(Guid.NewGuid(), "Admin"));
        request.Content = JsonContent.Create(new { reviewTemperature = (float?)null });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.TryGetProperty("reviewTemperature", out var reviewTemperature));
        Assert.Equal(JsonValueKind.Null, reviewTemperature.ValueKind);
    }

    // --- DELETE /admin/crawl-configurations/{configId} ---

    [Fact]
    public void DeleteCrawlConfig_ConflictResponseDescribesItsStringRefusal()
    {
        var action = typeof(MeisterDev.ProPR.Api.Controllers.AdminCrawlConfigsController).GetMethod("DeleteCrawlConfiguration")!;
        var response = action.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute), true)
            .Cast<Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute>().Single(attribute => attribute.StatusCode == 409);
        Assert.Equal(typeof(string), response.Type);
    }

    [Fact]
    public async Task DeleteCrawlConfig_RepositoryRejectsDeletion_Returns409WithoutSuccessLog()
    {
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<MeisterDev.ProPR.Api.Controllers.AdminCrawlConfigsController>>();
        logger.IsEnabled(Arg.Any<Microsoft.Extensions.Logging.LogLevel>()).Returns(true);
        var repository = Substitute.For<ICrawlConfigurationRepository>();
        repository.GetByIdAsync(factory.TestConfigId, Arg.Any<CancellationToken>()).Returns(
            new CrawlConfigurationDto(
                factory.TestConfigId, factory.TestClientId, ScmProvider.AzureDevOps, "https://dev.azure.com/testorg", "TestProject", 60, false,
                DateTimeOffset.UtcNow, []));
        repository.DeleteAsync(factory.TestConfigId, factory.TestClientId, Arg.Any<CancellationToken>()).Returns(false);
        using var rejecting = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton(logger);
            services.RemoveAll<ICrawlConfigurationRepository>();
            services.AddSingleton(repository);
        }));
        using var client = rejecting.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/admin/crawl-configurations/{factory.TestConfigId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateUserToken(Guid.NewGuid(), "Admin"));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.DoesNotContain(
            logger.ReceivedCalls(), call => call.GetMethodInfo().Name == "Log" &&
                                            call.GetArguments()[0] is Microsoft.Extensions.Logging.LogLevel.Information &&
                                            call.GetArguments()[1] is Microsoft.Extensions.Logging.EventId { Name: "LogCrawlConfigDeleted" });
    }

    [Fact]
    public async Task DeleteCrawlConfig_AdminDeletesExisting_Returns204()
    {
        var configId = factory.TestConfigId;

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/admin/crawl-configurations/{configId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(Guid.NewGuid(), "Admin"));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task DeleteCrawlConfig_NotFound_Returns404()
    {
        var nonExistentId = Guid.NewGuid();

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/admin/crawl-configurations/{nonExistentId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(Guid.NewGuid(), "Admin"));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteCrawlConfig_NonAdminUnownedConfig_Returns403()
    {
        var userId = factory.TestUserId;
        var unownedConfigId = factory.UnownedConfigId; // owned by a different client
        var token = factory.GenerateUserToken(userId);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/admin/crawl-configurations/{unownedConfigId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // --- Factory ---

    public sealed class AdminCrawlConfigsApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-jwt-secret-for-integration-tests-abc123";

        /// <summary>A client ID that belongs to the test user's assignments.</summary>
        public Guid TestClientId { get; } = Guid.NewGuid();

        /// <summary>The test user's ID (has 1 client assignment: TestClientId).</summary>
        public Guid TestUserId { get; } = Guid.NewGuid();

        /// <summary>A config ID owned by <see cref="TestClientId" />.</summary>
        public Guid TestConfigId { get; } = Guid.NewGuid();

        /// <summary>A config owned by a different client (not TestClientId).</summary>
        public Guid UnownedConfigId { get; } = Guid.NewGuid();

        private Guid UnownedClientId { get; } = Guid.NewGuid();
        public Guid GuidedOrganizationScopeId { get; } = Guid.NewGuid();
        public Guid GuidedProCursorSourceId { get; } = Guid.NewGuid();

        public IProviderAdminDiscoveryService AdoDiscoveryService { get; } =
            MeisterDev.ProPR.TestSupport.AdoGuidedDiscoveryTestSupport.Create();

        public IScmProviderRegistry ProviderRegistry { get; } = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();

        public IProCursorKnowledgeSourceRepository ProCursorKnowledgeSourceRepository { get; } =
            Substitute.For<IProCursorKnowledgeSourceRepository>();

        public ILicensingCapabilityService LicensingCapabilityService { get; } =
            Substitute.For<ILicensingCapabilityService>();

        public void SetCrawlConfigsCapabilityAvailability(bool isAvailable, string? message = null)
        {
            this.LicensingCapabilityService.GetCapabilityAsync(PremiumCapabilityKey.CrawlConfigs, Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult(
                        new CapabilitySnapshot(
                            PremiumCapabilityKey.CrawlConfigs,
                            PremiumCapabilityKey.CrawlConfigs,
                            true,
                            PremiumCapabilityOverrideState.Default,
                            isAvailable,
                            message)));
        }

        /// <summary>Generates a JWT token for the given user ID and role.</summary>
        public string GenerateUserToken(Guid userId, string globalRole = "User")
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var descriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(
                    new[]
                    {
                        new Claim("sub", userId.ToString()),
                        new Claim("global_role", globalRole),
                    }),
                Expires = DateTime.UtcNow.AddHours(1),
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
                Issuer = "meisterpropr",
                Audience = "meisterpropr",
            };
            return handler.WriteToken(handler.CreateToken(descriptor));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("DB_CONNECTION_STRING", string.Empty);
            builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
            builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
            builder.UseSetting("MEISTER_CLIENT_KEYS", "test-client-key-placeholder");
            builder.UseSetting("MEISTER_ADMIN_KEY", ValidAdminKey);
            builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

            var testClientId = this.TestClientId;
            var testUserId = this.TestUserId;
            var testConfigId = this.TestConfigId;
            var unownedConfigId = this.UnownedConfigId;
            var unownedClientId = this.UnownedClientId;
            var guidedOrganizationScopeId = this.GuidedOrganizationScopeId;
            var guidedProCursorSourceId = this.GuidedProCursorSourceId;

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();

                // Register IJwtTokenService for JWT Bearer token validation
                services.AddSingleton<IJwtTokenService, JwtTokenService>();

                // Stub ADO-dependent services
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedReviewDiscoveryService>());

                // Stub ICrawlConfigurationRepository
                services.AddScoped<ICrawlConfigurationRepository>(_ =>
                    CreateCrawlRepo(
                        testClientId,
                        testConfigId,
                        guidedOrganizationScopeId,
                        unownedConfigId,
                        unownedClientId));

                // Stub IUserRepository — test user owns TestClientId
                var userRepo = Substitute.For<IUserRepository>();
                var testUser = new AppUser
                {
                    Id = testUserId,
                    Username = "testuser",
                    GlobalRole = AppUserRole.User,
                    IsActive = true,
                };
                testUser.ClientAssignments.Add(
                    new UserClientRole
                    {
                        UserId = testUserId,
                        ClientId = testClientId,
                    });
                userRepo.GetByIdWithAssignmentsAsync(testUserId, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AppUser?>(testUser));
                userRepo.GetByIdWithAssignmentsAsync(
                        Arg.Is<Guid>(id => id != testUserId),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AppUser?>(null));
                services.AddSingleton(userRepo);

                // Stub IClientAdminService
                var clientAdminService = Substitute.For<IClientAdminService>();
                clientAdminService.ExistsAsync(testClientId, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(true));
                clientAdminService.ExistsAsync(
                        Arg.Is<Guid>(id => id != testClientId),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(false));
                services.AddSingleton(clientAdminService);

                this.AdoDiscoveryService.Provider.Returns(ScmProvider.AzureDevOps);
                this.AdoDiscoveryService.GetScopeAsync(
                        testClientId,
                        guidedOrganizationScopeId,
                        Arg.Any<CancellationToken>())
                    .Returns(
                        Task.FromResult<ClientScmScopeDto?>(
                            new ClientScmScopeDto(
                                guidedOrganizationScopeId,
                                testClientId,
                                Guid.NewGuid(),
                                "organization",
                                "testorg",
                                "https://dev.azure.com/testorg",
                                "Test Org",
                                "verified",
                                true,
                                DateTimeOffset.UtcNow,
                                null,
                                DateTimeOffset.UtcNow,
                                DateTimeOffset.UtcNow)));
                this.AdoDiscoveryService.GetScopeAsync(
                        Arg.Is<Guid>(clientId => clientId != testClientId),
                        Arg.Any<Guid>(),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<ClientScmScopeDto?>(null));

                this.AdoDiscoveryService.ListCrawlFilterOptionsAsync(
                        testClientId,
                        guidedOrganizationScopeId,
                        Arg.Any<string>(),
                        Arg.Any<CancellationToken>())
                    .Returns(callInfo => Task.FromResult<IReadOnlyList<ScmDiscoveryCrawlFilterOption>>(
                    [
                        new ScmDiscoveryCrawlFilterOption(
                            new CanonicalSourceReferenceDto("azureDevOps", "repo-1"),
                            "Repository One",
                            [new ScmDiscoveryBranchOption("main", true)]),
                        new ScmDiscoveryCrawlFilterOption(
                            new CanonicalSourceReferenceDto("azureDevOps", "repo-2"),
                            "Repository Two",
                            [new ScmDiscoveryBranchOption("develop", true)]),
                    ]));
                this.ProviderRegistry.GetProviderAdminDiscoveryService(ScmProvider.AzureDevOps)
                    .Returns(this.AdoDiscoveryService);
                services.AddSingleton(this.AdoDiscoveryService);
                this.ProviderRegistry.GetReviewSourcePolicy(Arg.Any<ScmProvider>()).Returns(call => ReviewSourcePolicies.Get(call.Arg<ScmProvider>()));
                services.AddSingleton(this.ProviderRegistry);

                var guidedSource = new ProCursorKnowledgeSource(
                    guidedProCursorSourceId,
                    testClientId,
                    "Guided Knowledge Source",
                    ProCursorSourceKind.Repository,
                    "https://dev.azure.com/testorg",
                    "TestProject",
                    "repo-1",
                    "main",
                    null,
                    true,
                    "auto");
                this.ProCursorKnowledgeSourceRepository.ListByClientAsync(testClientId, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<ProCursorKnowledgeSource>>([guidedSource]));
                this.ProCursorKnowledgeSourceRepository.ListByClientAsync(
                        Arg.Is<Guid>(clientId => clientId != testClientId),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<ProCursorKnowledgeSource>>([]));
                services.AddSingleton(this.ProCursorKnowledgeSourceRepository);
                this.SetCrawlConfigsCapabilityAvailability(true);
                services.AddSingleton(this.LicensingCapabilityService);

                // Stub IClientRegistry
                services.AddSingleton(Substitute.For<IClientRegistry>());
                services.AddSingleton(Substitute.For<IJobRepository>());
            });
        }

        private static ICrawlConfigurationRepository CreateCrawlRepo(
            Guid testClientId,
            Guid testConfigId,
            Guid guidedOrganizationScopeId,
            Guid unownedConfigId,
            Guid unownedClientId)
        {
            var crawlRepo = Substitute.For<ICrawlConfigurationRepository>();

            var testConfig = new CrawlConfigurationDto(
                testConfigId,
                testClientId,
                ScmProvider.AzureDevOps,
                "https://dev.azure.com/testorg",
                "TestProject",
                60,
                true,
                DateTimeOffset.UtcNow,
                [],
                guidedOrganizationScopeId,
                ReviewTemperature: 0.35f);

            var unownedConfig = new CrawlConfigurationDto(
                unownedConfigId,
                unownedClientId,
                ScmProvider.AzureDevOps,
                "https://dev.azure.com/other",
                "OtherProject",
                60,
                true,
                DateTimeOffset.UtcNow,
                []);

            var configsById = new Dictionary<Guid, CrawlConfigurationDto>
            {
                [testConfig.Id] = testConfig,
                [unownedConfig.Id] = unownedConfig,
            };

            var pausedConfig = new CrawlConfigurationDto(
                Guid.NewGuid(),
                testClientId,
                ScmProvider.AzureDevOps,
                "https://dev.azure.com/testorg",
                "PausedProject",
                120,
                false,
                DateTimeOffset.UtcNow,
                [],
                guidedOrganizationScopeId);

            configsById[pausedConfig.Id] = pausedConfig;

            // Admin GET: GetAllAsync
            crawlRepo.GetAllAsync(Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>(configsById.Values.ToList().AsReadOnly()));

            crawlRepo.GetAllActiveAsync(Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>(configsById.Values.Where(config => config.IsActive).ToList().AsReadOnly()));

            // User GET: GetByClientIdsAsync
            crawlRepo.GetByClientIdsAsync(
                    Arg.Any<IEnumerable<Guid>>(),
                    Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var clientIds = callInfo.ArgAt<IEnumerable<Guid>>(0).ToHashSet();
                    var configs = configsById.Values
                        .Where(config => clientIds.Contains(config.ClientId))
                        .ToList()
                        .AsReadOnly();
                    return Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>(configs);
                });

            // GetByIdAsync: testConfig exists; unownedConfig exists too; others not found
            crawlRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var configId = callInfo.ArgAt<Guid>(0);
                    return Task.FromResult(
                        configsById.TryGetValue(configId, out var config)
                            ? config
                            : null);
                });

            // Apply updates to existing configurations and report unknown identities.
            crawlRepo.UpdateWithResultAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<int?>(),
                    Arg.Any<bool?>(),
                    Arg.Any<Guid?>(),
                    Arg.Any<CancellationToken>(),
                    Arg.Any<float?>(),
                    Arg.Any<bool>(),
                    Arg.Any<IReadOnlyList<CrawlRepoFilterDto>?>())
                .Returns(callInfo =>
                {
                    var configId = callInfo.ArgAt<Guid>(0);
                    if (!configsById.TryGetValue(configId, out var existingConfig))
                    {
                        return Task.FromResult(CrawlConfigurationUpdateResult.NotFound);
                    }

                    var shouldUpdateReviewTemperature = callInfo.ArgAt<bool>(6);
                    var reviewTemperature = callInfo.ArgAt<float?>(5);

                    var updatedConfig = existingConfig with
                    {
                        CrawlIntervalSeconds = callInfo.ArgAt<int?>(1) ?? existingConfig.CrawlIntervalSeconds,
                        IsActive = callInfo.ArgAt<bool?>(2) ?? existingConfig.IsActive,
                        ReviewTemperature = shouldUpdateReviewTemperature ? reviewTemperature : existingConfig.ReviewTemperature,
                        RepoFilters = callInfo.ArgAt<IReadOnlyList<CrawlRepoFilterDto>?>(7) ?? existingConfig.RepoFilters,
                    };
                    configsById[configId] = updatedConfig;
                    return Task.FromResult(CrawlConfigurationUpdateResult.Updated);
                });

            // DeleteAsync: returns true for testConfig or unownedConfig (ownership checked in controller)
            crawlRepo.DeleteAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(callInfo => Task.FromResult(configsById.Remove(callInfo.ArgAt<Guid>(0))));

            // POST: AddAsync
            crawlRepo.AddAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<ScmProvider>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<int>(),
                    Arg.Any<Guid?>(),
                    Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var createdConfig = new CrawlConfigurationDto(
                        Guid.NewGuid(),
                        callInfo.ArgAt<Guid>(0),
                        callInfo.ArgAt<ScmProvider>(1),
                        callInfo.ArgAt<string>(2),
                        callInfo.ArgAt<string>(3),
                        callInfo.ArgAt<int>(4),
                        true,
                        DateTimeOffset.UtcNow,
                        [],
                        callInfo.ArgAt<Guid?>(5));
                    configsById[createdConfig.Id] = createdConfig;
                    return Task.FromResult(createdConfig);
                });

            crawlRepo.UpdateRepoFiltersAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<IReadOnlyList<CrawlRepoFilterDto>>(),
                    Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var configId = callInfo.ArgAt<Guid>(0);
                    if (!configsById.TryGetValue(configId, out var existingConfig))
                    {
                        return Task.FromResult(false);
                    }

                    configsById[configId] = existingConfig with
                    {
                        RepoFilters = callInfo.ArgAt<IReadOnlyList<CrawlRepoFilterDto>>(1),
                    };
                    return Task.FromResult(true);
                });

            crawlRepo.UpdateSourceScopeAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<ProCursorSourceScopeMode>(),
                    Arg.Any<IReadOnlyList<Guid>>(),
                    Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var configId = callInfo.ArgAt<Guid>(0);
                    if (!configsById.TryGetValue(configId, out var existingConfig))
                    {
                        return Task.FromResult(false);
                    }

                    var selectedSourceIds = callInfo.ArgAt<IReadOnlyList<Guid>>(2).Distinct().ToList().AsReadOnly();
                    configsById[configId] = existingConfig with
                    {
                        ProCursorSourceScopeMode = callInfo.ArgAt<ProCursorSourceScopeMode>(1),
                        ProCursorSourceIds = selectedSourceIds,
                    };
                    return Task.FromResult(true);
                });

            // ExistsAsync: false by default (no duplicates)
            crawlRepo.ExistsAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string?>(),
                    Arg.Any<string?>(),
                    Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var clientId = callInfo.ArgAt<Guid>(0);
                    var organizationUrl = callInfo.ArgAt<string>(1);
                    var projectId = callInfo.ArgAt<string>(2);

                    return Task.FromResult(
                        configsById.Values.Any(config =>
                            config.ClientId == clientId &&
                            string.Equals(
                                config.ProviderScopePath,
                                organizationUrl,
                                StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(config.ProviderProjectKey, projectId, StringComparison.OrdinalIgnoreCase)));
                });

            return crawlRepo;
        }
    }
}
