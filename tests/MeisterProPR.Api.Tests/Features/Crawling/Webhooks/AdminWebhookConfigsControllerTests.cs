// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.Features.Crawling.Webhooks.Dtos;
using MeisterProPR.Application.Features.Crawling.Webhooks.Ports;
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

namespace MeisterProPR.Api.Tests.Features.Crawling.Webhooks;

public sealed class AdminWebhookConfigsControllerTests(AdminWebhookConfigsControllerTests.AdminWebhookConfigsApiFactory factory)
    : IClassFixture<AdminWebhookConfigsControllerTests.AdminWebhookConfigsApiFactory>
{
    [Fact]
    public async Task GetWebhookConfigurations_WithAdminJwt_ReturnsAllConfigs()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/webhook-configurations");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(Guid.NewGuid(), "Admin"));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Equal(2, body.GetArrayLength());
    }

    [Fact]
    public async Task PostWebhookConfiguration_AdminWithGuidedSelections_Returns201WithListenerAndSecret()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/webhook-configurations");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(Guid.NewGuid(), "Admin"));
        request.Content = JsonContent.Create(
            new
            {
                clientId = factory.TestClientId,
                provider = "azureDevOps",
                organizationScopeId = factory.GuidedOrganizationScopeId,
                providerProjectKey = "GuidedProject",
                enabledEvents = new[] { "pullRequestCreated", "pullRequestUpdated", "pullRequestCommented" },
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
            });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(factory.TestClientId, body.GetProperty("clientId").GetGuid());
        Assert.Equal("guided-secret", body.GetProperty("generatedSecret").GetString());
        Assert.Contains(
            "/webhooks/v1/providers/ado/",
            body.GetProperty("listenerUrl").GetString(),
            StringComparison.Ordinal);
        Assert.Equal("https://dev.azure.com/testorg", body.GetProperty("providerScopePath").GetString());
        Assert.Equal("GuidedProject", body.GetProperty("providerProjectKey").GetString());
        Assert.Equal(1, body.GetProperty("repoFilters").GetArrayLength());
    }

    [Theory]
    [InlineData("gitLab", "https://gitlab.example.com", "acme/platform", "gitlab")]
    [InlineData("forgejo", "https://codeberg.org", "acme-labs", "forgejo")]
    public async Task PostWebhookConfiguration_AdminWithManualProviderSelection_ReturnsProviderScopedListener(
        string provider,
        string organizationUrl,
        string projectId,
        string expectedPathSegment)
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/webhook-configurations");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(Guid.NewGuid(), "Admin"));
        request.Content = JsonContent.Create(
            new
            {
                clientId = factory.TestClientId,
                provider,
                providerScopePath = organizationUrl,
                providerProjectKey = projectId,
                enabledEvents = new[] { "pullRequestUpdated" },
                repoFilters = new[]
                {
                    new
                    {
                        repositoryName = "propr",
                        targetBranchPatterns = Array.Empty<string>(),
                    },
                },
            });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(provider, body.GetProperty("provider").GetString());
        Assert.Equal(organizationUrl, body.GetProperty("providerScopePath").GetString());
        Assert.Equal(projectId, body.GetProperty("providerProjectKey").GetString());
        Assert.Contains(
            $"/webhooks/v1/providers/{expectedPathSegment}/",
            body.GetProperty("listenerUrl").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetWebhookConfigurationDeliveries_WithOwnedConfig_ReturnsRecentItems()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/webhook-configurations/{factory.TestConfigId}/deliveries");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(factory.TestUserId));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var items = body.GetProperty("items");
        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal("git.pullrequest.updated", items[0].GetProperty("eventType").GetString());
        Assert.Equal("accepted", items[0].GetProperty("deliveryOutcome").GetString()?.ToLowerInvariant());
        Assert.Contains(
            "Submitted review intake job",
            items[0].GetProperty("actionSummaries")[0].GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetWebhookConfigurationDeliveries_TakeAboveMaximum_ClampsTo200()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/webhook-configurations/{factory.TestConfigId}/deliveries?take=999");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(factory.TestUserId));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await factory.WebhookDeliveryLogRepository.Received(1)
            .ListByWebhookConfigurationAsync(factory.TestConfigId, 200, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetWebhookConfigurationDeliveries_NonPositiveTake_ClampsTo1()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/webhook-configurations/{factory.TestConfigId}/deliveries?take=0");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(factory.TestUserId));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await factory.WebhookDeliveryLogRepository.Received(1)
            .ListByWebhookConfigurationAsync(factory.TestConfigId, 1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchWebhookConfiguration_AdminUpdatesEnabledEventsAndScope_Returns200()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/admin/webhook-configurations/{factory.TestConfigId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(Guid.NewGuid(), "Admin"));
        request.Content = JsonContent.Create(
            new
            {
                isActive = false,
                enabledEvents = new[] { "pullRequestUpdated" },
                repoFilters = Array.Empty<object>(),
            });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.False(body.GetProperty("isActive").GetBoolean());
        Assert.Single(body.GetProperty("enabledEvents").EnumerateArray());
        Assert.Equal(0, body.GetProperty("repoFilters").GetArrayLength());
    }

    [Fact]
    public async Task PostWebhookConfiguration_InvalidGuidedFilter_ReturnsConflictWithSafeError()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/webhook-configurations");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(Guid.NewGuid(), "Admin"));
        request.Content = JsonContent.Create(
            new
            {
                clientId = factory.TestClientId,
                provider = "azureDevOps",
                organizationScopeId = factory.GuidedOrganizationScopeId,
                providerProjectKey = "GuidedProject",
                enabledEvents = new[] { "pullRequestCreated" },
                repoFilters = new[]
                {
                    new
                    {
                        displayName = "Repository Two",
                        canonicalSourceRef = new
                        {
                            provider = "azureDevOps",
                            value = "repo-2",
                        },
                        targetBranchPatterns = new[] { "main" },
                    },
                },
            });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(
            "The selected webhook configuration is no longer valid. Refresh the provider selections and try again.",
            body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PatchWebhookConfiguration_InvalidGuidedFilter_ReturnsConflictWithSafeError()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/admin/webhook-configurations/{factory.TestConfigId}");
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
                        displayName = "Repository Two",
                        canonicalSourceRef = new
                        {
                            provider = "azureDevOps",
                            value = "repo-2",
                        },
                        targetBranchPatterns = new[] { "main" },
                    },
                },
            });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(
            "The selected webhook configuration is no longer valid. Refresh the provider selections and try again.",
            body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task DeleteWebhookConfiguration_NonAdminForUnownedConfig_Returns403()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/admin/webhook-configurations/{factory.UnownedConfigId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUserToken(factory.TestUserId));

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    public sealed class AdminWebhookConfigsApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-jwt-secret-for-webhook-admin-tests-abc123";

        public Guid TestClientId { get; } = Guid.NewGuid();
        public Guid TestUserId { get; } = Guid.NewGuid();
        public Guid TestConfigId { get; } = Guid.NewGuid();
        public Guid UnownedConfigId { get; } = Guid.NewGuid();
        public Guid UnownedClientId { get; } = Guid.NewGuid();
        public Guid GuidedOrganizationScopeId { get; } = Guid.NewGuid();

        public IWebhookDeliveryLogRepository WebhookDeliveryLogRepository { get; } =
            Substitute.For<IWebhookDeliveryLogRepository>();

        public IProviderAdminDiscoveryService DiscoveryService { get; } =
            Substitute.For<IProviderAdminDiscoveryService>();

        public IScmProviderRegistry ProviderRegistry { get; } = Substitute.For<IScmProviderRegistry>();

        public string GenerateUserToken(Guid userId, string globalRole = "User")
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var descriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(
                [
                    new Claim("sub", userId.ToString()),
                    new Claim("global_role", globalRole),
                ]),
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
            builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

            var testClientId = this.TestClientId;
            var testUserId = this.TestUserId;
            var testConfigId = this.TestConfigId;
            var unownedConfigId = this.UnownedConfigId;
            var unownedClientId = this.UnownedClientId;
            var guidedOrganizationScopeId = this.GuidedOrganizationScopeId;

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();

                services.AddSingleton<IJwtTokenService, JwtTokenService>();
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedReviewDiscoveryService>());
                services.AddSingleton(Substitute.For<IClientRegistry>());
                services.AddSingleton(Substitute.For<IJobRepository>());
                services.AddSingleton(this.ProviderRegistry);

                services.AddScoped<IWebhookConfigurationRepository>(_ =>
                    CreateWebhookRepo(
                        testClientId,
                        testConfigId,
                        guidedOrganizationScopeId,
                        unownedConfigId,
                        unownedClientId));
                services.AddScoped<IWebhookDeliveryLogRepository>(_ => CreateDeliveryRepo(
                    this.WebhookDeliveryLogRepository,
                    testConfigId));

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
                        Role = ClientRole.ClientAdministrator,
                    });
                userRepo.GetByIdWithAssignmentsAsync(testUserId, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AppUser?>(testUser));
                userRepo.GetByIdWithAssignmentsAsync(Arg.Is<Guid>(id => id != testUserId), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AppUser?>(null));
                userRepo.GetUserClientRolesAsync(testUserId, Arg.Any<CancellationToken>())
                    .Returns(
                        Task.FromResult(
                            new Dictionary<Guid, ClientRole>
                            {
                                [testClientId] = ClientRole.ClientAdministrator,
                            }));
                userRepo.GetUserClientRolesAsync(Arg.Is<Guid>(id => id != testUserId), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>()));
                services.AddSingleton(userRepo);

                var clientAdminService = Substitute.For<IClientAdminService>();
                clientAdminService.ExistsAsync(testClientId, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(true));
                clientAdminService.ExistsAsync(Arg.Is<Guid>(id => id != testClientId), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(false));
                services.AddSingleton(clientAdminService);

                this.DiscoveryService.Provider.Returns(ScmProvider.AzureDevOps);
                this.DiscoveryService.GetScopeAsync(
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
                this.DiscoveryService.GetScopeAsync(
                        Arg.Is<Guid>(id => id != testClientId),
                        Arg.Any<Guid>(),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<ClientScmScopeDto?>(null));
                this.DiscoveryService.ListCrawlFiltersAsync(
                        testClientId,
                        guidedOrganizationScopeId,
                        Arg.Any<string>(),
                        Arg.Any<CancellationToken>())
                    .Returns(
                        Task.FromResult<IReadOnlyList<AdoCrawlFilterOptionDto>>(
                        [
                            new AdoCrawlFilterOptionDto(
                                new CanonicalSourceReferenceDto("azureDevOps", "repo-1"),
                                "Repository One",
                                [new AdoBranchOptionDto("main", true)]),
                        ]));
                this.ProviderRegistry.GetProviderAdminDiscoveryService(ScmProvider.AzureDevOps)
                    .Returns(this.DiscoveryService);
                services.AddSingleton(this.DiscoveryService);

                var secretGenerator = Substitute.For<IWebhookSecretGenerator>();
                secretGenerator.GenerateSecret().Returns("guided-secret");
                services.AddSingleton(secretGenerator);

                var secretCodec = Substitute.For<ISecretProtectionCodec>();
                secretCodec.Protect("guided-secret", "WebhookSecret").Returns("mpr-protected:v1:guided-secret");
                services.AddSingleton(secretCodec);
            });
        }

        private static IWebhookConfigurationRepository CreateWebhookRepo(
            Guid testClientId,
            Guid testConfigId,
            Guid guidedOrganizationScopeId,
            Guid unownedConfigId,
            Guid unownedClientId)
        {
            var webhookRepo = Substitute.For<IWebhookConfigurationRepository>();

            var testConfig = new WebhookConfigurationDto(
                testConfigId,
                testClientId,
                WebhookProviderType.AzureDevOps,
                "path-key-1",
                "https://dev.azure.com/testorg",
                "TestProject",
                true,
                DateTimeOffset.UtcNow,
                [WebhookEventType.PullRequestCreated, WebhookEventType.PullRequestUpdated],
                [
                    new WebhookRepoFilterDto(
                        Guid.NewGuid(),
                        "Repository One",
                        ["main"],
                        new CanonicalSourceReferenceDto("azureDevOps", "repo-1"),
                        "Repository One"),
                ],
                guidedOrganizationScopeId,
                SecretCiphertext: "mpr-protected:v1:existing");

            var unownedConfig = new WebhookConfigurationDto(
                unownedConfigId,
                unownedClientId,
                WebhookProviderType.AzureDevOps,
                "path-key-2",
                "https://dev.azure.com/other",
                "OtherProject",
                true,
                DateTimeOffset.UtcNow,
                [WebhookEventType.PullRequestUpdated],
                [],
                SecretCiphertext: "mpr-protected:v1:other");

            var configsById = new Dictionary<Guid, WebhookConfigurationDto>
            {
                [testConfig.Id] = testConfig,
                [unownedConfig.Id] = unownedConfig,
            };

            webhookRepo.GetAllAsync(Arg.Any<CancellationToken>())
                .Returns(_ =>
                    Task.FromResult<IReadOnlyList<WebhookConfigurationDto>>(configsById.Values.ToList().AsReadOnly()));

            webhookRepo.GetByClientIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var clientIds = callInfo.ArgAt<IEnumerable<Guid>>(0).ToHashSet();
                    return Task.FromResult<IReadOnlyList<WebhookConfigurationDto>>(
                        configsById.Values.Where(config => clientIds.Contains(config.ClientId)).ToList().AsReadOnly());
                });

            webhookRepo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var configId = callInfo.ArgAt<Guid>(0);
                    return Task.FromResult(configsById.TryGetValue(configId, out var config) ? config : null);
                });

            webhookRepo.AddAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<WebhookProviderType>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<IReadOnlyList<WebhookEventType>>(),
                    Arg.Any<Guid?>(),
                    Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var created = new WebhookConfigurationDto(
                        Guid.NewGuid(),
                        callInfo.ArgAt<Guid>(0),
                        callInfo.ArgAt<WebhookProviderType>(1),
                        callInfo.ArgAt<string>(2),
                        callInfo.ArgAt<string>(3),
                        callInfo.ArgAt<string>(4),
                        true,
                        DateTimeOffset.UtcNow,
                        callInfo.ArgAt<IReadOnlyList<WebhookEventType>>(6),
                        [],
                        callInfo.ArgAt<Guid?>(7),
                        SecretCiphertext: callInfo.ArgAt<string>(5));

                    configsById[created.Id] = created;
                    return Task.FromResult(created);
                });

            webhookRepo.UpdateAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<bool?>(),
                    Arg.Any<IReadOnlyList<WebhookEventType>?>(),
                    Arg.Any<Guid?>(),
                    Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var configId = callInfo.ArgAt<Guid>(0);
                    if (!configsById.TryGetValue(configId, out var existing))
                    {
                        return Task.FromResult(false);
                    }

                    configsById[configId] = existing with
                    {
                        IsActive = callInfo.ArgAt<bool?>(1) ?? existing.IsActive,
                        EnabledEvents = callInfo.ArgAt<IReadOnlyList<WebhookEventType>?>(2) ?? existing.EnabledEvents,
                    };

                    return Task.FromResult(true);
                });

            webhookRepo.UpdateRepoFiltersAsync(
                    Arg.Any<Guid>(),
                    Arg.Any<IReadOnlyList<WebhookRepoFilterDto>>(),
                    Arg.Any<CancellationToken>())
                .Returns(callInfo =>
                {
                    var configId = callInfo.ArgAt<Guid>(0);
                    if (!configsById.TryGetValue(configId, out var existing))
                    {
                        return Task.FromResult(false);
                    }

                    configsById[configId] = existing with
                    {
                        RepoFilters = callInfo.ArgAt<IReadOnlyList<WebhookRepoFilterDto>>(1),
                    };

                    return Task.FromResult(true);
                });

            webhookRepo.DeleteAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(callInfo => Task.FromResult(configsById.Remove(callInfo.ArgAt<Guid>(0))));

            return webhookRepo;
        }

        private static IWebhookDeliveryLogRepository CreateDeliveryRepo(
            IWebhookDeliveryLogRepository deliveryRepo,
            Guid testConfigId)
        {
            deliveryRepo.ListByWebhookConfigurationAsync(testConfigId, Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(
                    Task.FromResult<IReadOnlyList<WebhookDeliveryLogEntryDto>>(
                    [
                        new WebhookDeliveryLogEntryDto(
                            Guid.NewGuid(),
                            testConfigId,
                            DateTimeOffset.UtcNow,
                            "git.pullrequest.updated",
                            WebhookDeliveryOutcome.Accepted,
                            200,
                            "repo-1",
                            42,
                            "refs/heads/feature/test",
                            "refs/heads/main",
                            ["Submitted review intake job for PR #42 at iteration 7 via pull request updated."],
                            null),
                    ]));
            deliveryRepo.ListByWebhookConfigurationAsync(
                    Arg.Is<Guid>(id => id != testConfigId),
                    Arg.Any<int>(),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<WebhookDeliveryLogEntryDto>>([]));
            return deliveryRepo;
        }
    }
}
