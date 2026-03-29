using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
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
        request.Headers.Add("X-Admin-Key", ValidAdminKey);

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
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

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
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Content = JsonContent.Create(new
        {
            clientId = factory.TestClientId,
            organizationUrl = "https://dev.azure.com/myorg",
            projectId = "MyProject",
            crawlIntervalSeconds = 60,
        });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task PostCrawlConfig_NoCredentials_Returns401()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/crawl-configurations");
        request.Content = JsonContent.Create(new
        {
            clientId = factory.TestClientId,
            organizationUrl = "https://dev.azure.com/myorg",
            projectId = "MyProject",
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
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Content = JsonContent.Create(new
        {
            clientId = factory.TestClientId,
            organizationUrl = "https://dev.azure.com/myorg",
            projectId = "MyProject",
            crawlIntervalSeconds = 5,  // below minimum of 10
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
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new
        {
            clientId = unownedClientId,
            organizationUrl = "https://dev.azure.com/myorg",
            projectId = "MyProject",
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
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
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
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
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
        request.Headers.Add("X-Admin-Key", ValidAdminKey);
        request.Content = JsonContent.Create(new { crawlIntervalSeconds = 3 }); // below minimum

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --- DELETE /admin/crawl-configurations/{configId} ---

    [Fact]
    public async Task DeleteCrawlConfig_AdminDeletesExisting_Returns204()
    {
        var configId = factory.TestConfigId;

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/admin/crawl-configurations/{configId}");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task DeleteCrawlConfig_NotFound_Returns404()
    {
        var nonExistentId = Guid.NewGuid();

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"/admin/crawl-configurations/{nonExistentId}");
        request.Headers.Add("X-Admin-Key", ValidAdminKey);

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
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

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

        /// <summary>A config ID owned by <see cref="TestClientId"/>.</summary>
        public Guid TestConfigId { get; } = Guid.NewGuid();

        /// <summary>A config owned by a different client (not TestClientId).</summary>
        public Guid UnownedConfigId { get; } = Guid.NewGuid();
        private Guid UnownedClientId { get; } = Guid.NewGuid();

        /// <summary>Generates a JWT token for the given user ID and role.</summary>
        public string GenerateUserToken(Guid userId, string globalRole = "User")
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var descriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(new[]
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

            builder.ConfigureServices(services =>
            {
                // Register IJwtTokenService for JWT Bearer token validation
                services.AddSingleton<IJwtTokenService, JwtTokenService>();

                // Stub ADO-dependent services
                services.AddSingleton(Substitute.For<IAdoTokenValidator>());
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedPrFetcher>());

                // Stub ICrawlConfigurationRepository
                var crawlRepo = Substitute.For<ICrawlConfigurationRepository>();
                SetupCrawlRepo(crawlRepo, testClientId, testConfigId, unownedConfigId, unownedClientId);
                services.AddSingleton(crawlRepo);

                // Stub IUserRepository — test user owns TestClientId
                var userRepo = Substitute.For<IUserRepository>();
                var testUser = new Domain.Entities.AppUser
                {
                    Id = testUserId,
                    Username = "testuser",
                    GlobalRole = Domain.Enums.AppUserRole.User,
                    IsActive = true,
                };
                testUser.ClientAssignments.Add(new Domain.Entities.UserClientRole
                {
                    UserId = testUserId,
                    ClientId = testClientId,
                });
                userRepo.GetByIdWithAssignmentsAsync(testUserId, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<Domain.Entities.AppUser?>(testUser));
                userRepo.GetByIdWithAssignmentsAsync(
                        Arg.Is<Guid>(id => id != testUserId),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<Domain.Entities.AppUser?>(null));
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

                // Stub IClientRegistry
                services.AddSingleton(Substitute.For<IClientRegistry>());
                services.AddSingleton(Substitute.For<IClientAdoCredentialRepository>());
            });
        }

        private static void SetupCrawlRepo(
            ICrawlConfigurationRepository crawlRepo,
            Guid testClientId,
            Guid testConfigId,
            Guid unownedConfigId,
            Guid unownedClientId)
        {
            var testConfig = new CrawlConfigurationDto(
                testConfigId,
                testClientId,
                "https://dev.azure.com/testorg",
                "TestProject",
                null,
                60,
                true,
                DateTimeOffset.UtcNow,
                []);

            var unownedConfig = new CrawlConfigurationDto(
                unownedConfigId,
                unownedClientId,
                "https://dev.azure.com/other",
                "OtherProject",
                null,
                60,
                true,
                DateTimeOffset.UtcNow,
                []);

            // Admin GET: GetAllActiveAsync
            crawlRepo.GetAllActiveAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([testConfig, unownedConfig]));

            // User GET: GetByClientIdsAsync
            crawlRepo.GetByClientIdsAsync(
                    Arg.Is<IEnumerable<Guid>>(ids => ids.Contains(testClientId)),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([testConfig]));
            crawlRepo.GetByClientIdsAsync(
                    Arg.Is<IEnumerable<Guid>>(ids => !ids.Any()),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([]));

            // GetByIdAsync: testConfig exists; unownedConfig exists too; others not found
            crawlRepo.GetByIdAsync(testConfigId, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<CrawlConfigurationDto?>(testConfig));
            crawlRepo.GetByIdAsync(unownedConfigId, Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<CrawlConfigurationDto?>(unownedConfig));
            crawlRepo.GetByIdAsync(
                    Arg.Is<Guid>(id => id != testConfigId && id != unownedConfigId),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<CrawlConfigurationDto?>(null));

            // UpdateAsync: returns true for testConfig, false for unknown
            crawlRepo.UpdateAsync(testConfigId, Arg.Any<int?>(), Arg.Any<bool?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(true));
            crawlRepo.UpdateAsync(
                    Arg.Is<Guid>(id => id != testConfigId),
                    Arg.Any<int?>(), Arg.Any<bool?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(false));

            // DeleteAsync: returns true for testConfig or unownedConfig (ownership checked in controller)
            crawlRepo.DeleteAsync(testConfigId, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(true));
            crawlRepo.DeleteAsync(
                    Arg.Is<Guid>(id => id != testConfigId),
                    Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(false));

            // POST: AddAsync
            crawlRepo.AddAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<int>(),
                    Arg.Any<CancellationToken>())
                .Returns(ci => Task.FromResult(new CrawlConfigurationDto(
                    Guid.NewGuid(),
                    ci.ArgAt<Guid>(0),
                    ci.ArgAt<string>(1),
                    ci.ArgAt<string>(2),
                    null,
                    ci.ArgAt<int>(3),
                    true,
                    DateTimeOffset.UtcNow,
                    [])));

            // ExistsAsync: false by default (no duplicates)
            crawlRepo.ExistsAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(false));
        }
    }
}
