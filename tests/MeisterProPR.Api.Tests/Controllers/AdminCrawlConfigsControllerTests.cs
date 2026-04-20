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
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Controllers;

/// <summary>Integration tests for <see cref="MeisterProPR.Api.Controllers.AdminCrawlConfigsController" />.</summary>
public sealed class AdminCrawlConfigsControllerTests(AdminCrawlConfigsControllerTests.AdminCrawlConfigsApiFactory factory)
    : IClassFixture<AdminCrawlConfigsControllerTests.AdminCrawlConfigsApiFactory>
{
    private const string ValidAdminKey = "admin-key-min-16-chars-ok";

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
                clientId = factory.TestClientId,
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
                clientId = factory.TestClientId,
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
                clientId = factory.TestClientId,
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
                clientId = factory.TestClientId,
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
                clientId = factory.TestClientId,
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
                clientId = factory.TestClientId,
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
                clientId = unownedClientId,
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
    public async Task PatchCrawlConfig_GuidedFilterThatNoLongerExists_Returns409()
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

    // --- DELETE /admin/crawl-configurations/{configId} ---

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
            Substitute.For<IProviderAdminDiscoveryService>();

        public IScmProviderRegistry ProviderRegistry { get; } = Substitute.For<IScmProviderRegistry>();

        public IProCursorKnowledgeSourceRepository ProCursorKnowledgeSourceRepository { get; } =
            Substitute.For<IProCursorKnowledgeSourceRepository>();

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

                this.AdoDiscoveryService.ListCrawlFiltersAsync(
                        testClientId,
                        guidedOrganizationScopeId,
                        Arg.Any<string>(),
                        Arg.Any<CancellationToken>())
                    .Returns(callInfo => Task.FromResult<IReadOnlyList<AdoCrawlFilterOptionDto>>(
                    [
                        new AdoCrawlFilterOptionDto(
                            new CanonicalSourceReferenceDto("azureDevOps", "repo-1"),
                            "Repository One",
                            [new AdoBranchOptionDto("main", true)]),
                        new AdoCrawlFilterOptionDto(
                            new CanonicalSourceReferenceDto("azureDevOps", "repo-2"),
                            "Repository Two",
                            [new AdoBranchOptionDto("develop", true)]),
                    ]));
                this.ProviderRegistry.GetProviderAdminDiscoveryService(ScmProvider.AzureDevOps)
                    .Returns(this.AdoDiscoveryService);
                services.AddSingleton(this.AdoDiscoveryService);
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
                null,
                60,
                true,
                DateTimeOffset.UtcNow,
                [],
                guidedOrganizationScopeId);

            var unownedConfig = new CrawlConfigurationDto(
                unownedConfigId,
                unownedClientId,
                ScmProvider.AzureDevOps,
                "https://dev.azure.com/other",
                "OtherProject",
                null,
                60,
                true,
                DateTimeOffset.UtcNow,
                []);

            var configsById = new Dictionary<Guid, CrawlConfigurationDto>
            {
                [testConfig.Id] = testConfig,
                [unownedConfig.Id] = unownedConfig,
            };

            // Admin GET: GetAllActiveAsync
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

            // UpdateAsync: returns true for testConfig, false for unknown
            crawlRepo.UpdateAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<int?>(),
                    Arg.Any<bool?>(),
                    Arg.Any<Guid?>(),
                    Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var configId = callInfo.ArgAt<Guid>(0);
                    if (!configsById.TryGetValue(configId, out var existingConfig))
                    {
                        return Task.FromResult(false);
                    }

                    var updatedConfig = existingConfig with
                    {
                        CrawlIntervalSeconds = callInfo.ArgAt<int?>(1) ?? existingConfig.CrawlIntervalSeconds,
                        IsActive = callInfo.ArgAt<bool?>(2) ?? existingConfig.IsActive,
                    };
                    configsById[configId] = updatedConfig;
                    return Task.FromResult(true);
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
                        null,
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
