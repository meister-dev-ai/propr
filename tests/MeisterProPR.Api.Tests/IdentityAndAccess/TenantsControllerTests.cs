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
using MeisterProPR.Application.Features.Licensing.Dtos;
using MeisterProPR.Application.Features.Licensing.Models;
using MeisterProPR.Application.Features.Licensing.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Auth;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess.Persistence;
using MeisterProPR.Infrastructure.Features.Licensing.Support;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace MeisterProPR.Api.Tests.IdentityAndAccess;

public sealed class TenantsControllerTests(TenantAdministrationApiFactory factory)
    : IClassFixture<TenantAdministrationApiFactory>
{
    [Fact]
    public async Task PostTenant_PlatformAdmin_Returns201AndPersistsTenant()
    {
        factory.ResetLicensing();

        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/tenants");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(Guid.NewGuid(), AppUserRole.Admin));
        request.Content = JsonContent.Create(new { slug = tenantSlug, displayName = "Acme Corp" });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(tenantSlug, body.GetProperty("slug").GetString());
        Assert.Equal("Acme Corp", body.GetProperty("displayName").GetString());
        Assert.True(body.GetProperty("localLoginEnabled").GetBoolean());

        using var scope = factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        Assert.True(await dbContext.Tenants.AnyAsync(tenant => tenant.Slug == tenantSlug));
    }

    [Fact]
    public async Task PatchTenant_TenantAdministratorForSameTenant_Returns200AndUpdatesPolicy()
    {
        factory.ResetLicensing();

        var tenantId = await factory.SeedTenantAsync("acme", "Acme Corp");
        var userId = await factory.SeedUserAsync("tenant.admin", "tenant.admin@acme.test");
        await factory.SeedTenantMembershipAsync(tenantId, userId, TenantRole.TenantAdministrator);

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/admin/tenants/{tenantId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(userId, AppUserRole.User));
        request.Content = JsonContent.Create(new { displayName = "Acme Identity", localLoginEnabled = false });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Acme Identity", body.GetProperty("displayName").GetString());
        Assert.False(body.GetProperty("localLoginEnabled").GetBoolean());
    }

    [Fact]
    public async Task PatchTenant_TenantAdministratorForOtherTenant_Returns403()
    {
        factory.ResetLicensing();

        var ownTenantId = await factory.SeedTenantAsync("acme", "Acme Corp");
        var otherTenantId = await factory.SeedTenantAsync("globex", "Globex Corp");
        var userId = await factory.SeedUserAsync("tenant.admin", "tenant.admin@acme.test");
        await factory.SeedTenantMembershipAsync(ownTenantId, userId, TenantRole.TenantAdministrator);

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/admin/tenants/{otherTenantId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(userId, AppUserRole.User));
        request.Content = JsonContent.Create(new { displayName = "Globex Identity", localLoginEnabled = false });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ListTenants_PlatformAdmin_IncludesSystemTenantWithReadOnlyMetadata()
    {
        factory.ResetLicensing();

        var tenantId = await factory.SeedTenantAsync($"acme-{Guid.NewGuid():N}", "Acme Corp");

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/tenants");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(Guid.NewGuid(), AppUserRole.Admin));

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var tenants = body.EnumerateArray().ToList();
        Assert.Contains(
            tenants,
            tenant => tenant.GetProperty("id").GetGuid() == tenantId && tenant.GetProperty("isEditable").GetBoolean());

        var systemTenant = tenants.Single(tenant => tenant.GetProperty("id").GetGuid() == TenantCatalog.SystemTenantId);
        Assert.Equal(TenantCatalog.SystemTenantSlug, systemTenant.GetProperty("slug").GetString());
        Assert.Equal(TenantCatalog.SystemTenantDisplayName, systemTenant.GetProperty("displayName").GetString());
        Assert.False(systemTenant.GetProperty("localLoginEnabled").GetBoolean());
        Assert.False(systemTenant.GetProperty("isEditable").GetBoolean());
    }

    [Fact]
    public async Task PatchTenant_SystemTenant_Returns409Conflict()
    {
        factory.ResetLicensing();

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/admin/tenants/{TenantCatalog.SystemTenantId}");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(Guid.NewGuid(), AppUserRole.Admin));
        request.Content = JsonContent.Create(new { displayName = "Renamed System", localLoginEnabled = true });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("The internal System tenant cannot be modified.", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ListTenants_CommunityEdition_ReturnsOnlySystemTenant()
    {
        factory.ResetLicensing();
        factory.SetEdition(InstallationEdition.Community);
        await factory.SeedTenantAsync($"acme-{Guid.NewGuid():N}", "Acme Corp");

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/tenants");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(Guid.NewGuid(), AppUserRole.Admin));

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var tenants = body.EnumerateArray().ToList();
        Assert.Single(tenants);
        Assert.Equal(TenantCatalog.SystemTenantId, tenants[0].GetProperty("id").GetGuid());
        Assert.False(tenants[0].GetProperty("isEditable").GetBoolean());
    }

    [Fact]
    public async Task PostTenant_CommunityEdition_Returns409Conflict()
    {
        factory.ResetLicensing();
        factory.SetEdition(InstallationEdition.Community);

        var tenantSlug = $"acme-{Guid.NewGuid():N}";
        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/tenants");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateToken(Guid.NewGuid(), AppUserRole.Admin));
        request.Content = JsonContent.Create(new { slug = tenantSlug, displayName = "Acme Corp" });

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Community edition only supports the internal System tenant.", body.GetProperty("error").GetString());
    }
}

public sealed class TenantAdministrationApiFactory : WebApplicationFactory<Program>
{
    private const string TestJwtSecret = "test-tenant-admin-jwt-secret-32chars!";
    private readonly string _dbName = $"TestDb_TenantAdmin_{Guid.NewGuid()}";
    private readonly InMemoryDatabaseRoot _dbRoot = new();
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _externalAuthResponses = new();
    private readonly Lock _externalAuthResponsesLock = new();
    private readonly TestLicensingCapabilityService _licensingCapabilityService = new();
    private string? _publicBaseUrl;

    public void SetPublicBaseUrl(string? publicBaseUrl)
    {
        this._publicBaseUrl = publicBaseUrl;
    }

    public void SetSsoCapabilityAvailability(bool isAvailable, string? message = null)
    {
        this._licensingCapabilityService.SetCapabilityAvailability(
            PremiumCapabilityKey.SsoAuthentication,
            isAvailable,
            message);
    }

    public void ResetLicensing()
    {
        this._licensingCapabilityService.Reset();
    }

    public void SetEdition(InstallationEdition edition)
    {
        this._licensingCapabilityService.SetEdition(edition);
    }

    public void ResetExternalAuthResponses()
    {
        lock (this._externalAuthResponsesLock)
        {
            this._externalAuthResponses.Clear();
        }
    }

    public void QueueExternalAuthResponse(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        ArgumentNullException.ThrowIfNull(responder);

        lock (this._externalAuthResponsesLock)
        {
            this._externalAuthResponses.Enqueue(responder);
        }
    }

    public string GenerateToken(Guid userId, AppUserRole globalRole)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", userId.ToString()),
                new Claim("global_role", globalRole.ToString()),
                new Claim(JwtRegisteredClaimNames.UniqueName, "tenant-admin"),
            ]),
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
            Issuer = "meisterpropr",
            Audience = "meisterpropr",
        };

        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    public async Task<Guid> SeedTenantAsync(string slug, string displayName, bool localLoginEnabled = true)
    {
        using var scope = this.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var tenant = new TenantRecord
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            DisplayName = displayName,
            IsActive = true,
            LocalLoginEnabled = localLoginEnabled,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        dbContext.Tenants.Add(tenant);
        await dbContext.SaveChangesAsync();
        return tenant.Id;
    }

    public async Task<Guid> SeedUserAsync(string username, string email, AppUserRole globalRole = AppUserRole.User)
    {
        using var scope = this.Services.CreateScope();
        var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            PasswordHash = null,
            GlobalRole = globalRole,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await userRepository.AddAsync(user);
        return user.Id;
    }

    public async Task<string> GetUsernameAsync(Guid userId)
    {
        using var scope = this.Services.CreateScope();
        var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var user = await userRepository.GetByIdAsync(userId);
        return user?.Username ?? string.Empty;
    }

    public async Task SetLocalPasswordAsync(Guid userId, string password)
    {
        using var scope = this.Services.CreateScope();
        var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var passwordHashService = scope.ServiceProvider.GetRequiredService<IPasswordHashService>();
        await userRepository.UpdatePasswordHashAsync(userId, passwordHashService.Hash(password));
    }

    public async Task<Guid> SeedTenantMembershipAsync(Guid tenantId, Guid userId, TenantRole role)
    {
        using var scope = this.Services.CreateScope();
        var membershipService = scope.ServiceProvider.GetRequiredService<ITenantMembershipService>();
        var membership = await membershipService.UpsertAsync(tenantId, userId, role);
        return membership.Id;
    }

    public async Task SeedClientAssignmentAsync(Guid userId, Guid clientId, ClientRole role)
    {
        using var scope = this.Services.CreateScope();
        var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        await userRepository.AddClientAssignmentAsync(
            new UserClientRole
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ClientId = clientId,
                Role = role,
                AssignedAt = DateTimeOffset.UtcNow,
            });
    }

    public async Task<Guid> SeedClientAsync(Guid tenantId, string displayName)
    {
        using var scope = this.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var client = new ClientRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DisplayName = displayName,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        dbContext.Clients.Add(client);
        await dbContext.SaveChangesAsync();
        return client.Id;
    }

    public async Task<Guid> SeedSsoProviderAsync(
        Guid tenantId,
        string displayName,
        string providerKind = "EntraId",
        string protocolKind = "Oidc",
        string? issuerOrAuthorityUrl = null,
        string? clientId = null,
        string? clientSecret = "super-secret",
        IEnumerable<string>? scopes = null,
        IEnumerable<string>? allowedEmailDomains = null,
        bool isEnabled = true,
        bool autoCreateUsers = true)
    {
        using var scope = this.Services.CreateScope();
        var providerService = scope.ServiceProvider.GetRequiredService<ITenantSsoProviderService>();
        var provider = await providerService.CreateAsync(
            tenantId,
            displayName,
            providerKind,
            protocolKind,
            issuerOrAuthorityUrl ?? "https://login.example.test/oidc",
            clientId ?? $"client-{Guid.NewGuid():N}",
            clientSecret,
            scopes ?? ["openid", "profile", "email"],
            allowedEmailDomains ?? ["acme.test"],
            isEnabled,
            autoCreateUsers);

        return provider.Id;
    }

    public async Task SeedExternalIdentityAsync(
        Guid tenantId,
        Guid userId,
        Guid providerId,
        string issuer,
        string subject,
        string email,
        bool emailVerified = true)
    {
        using var scope = this.Services.CreateScope();
        var userRepository = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        await userRepository.AddExternalIdentityAsync(
            new ExternalIdentity
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                UserId = userId,
                SsoProviderId = providerId,
                Issuer = issuer,
                Subject = subject,
                Email = email,
                EmailVerified = emailVerified,
                CreatedAt = DateTimeOffset.UtcNow,
                LastSignInAt = DateTimeOffset.UtcNow,
            });
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
        builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
        builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
        builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

        if (!string.IsNullOrWhiteSpace(this._publicBaseUrl))
        {
            builder.UseSetting("MEISTER_PUBLIC_BASE_URL", this._publicBaseUrl);
        }

        var dbName = this._dbName;
        var dbRoot = this._dbRoot;
        builder.ConfigureServices(services =>
        {
            var secretProtectionCodec = Substitute.For<ISecretProtectionCodec>();
            secretProtectionCodec.Protect(Arg.Any<string>(), Arg.Any<string>())
                .Returns(callInfo => $"protected::{callInfo.ArgAt<string>(0)}");
            secretProtectionCodec.Unprotect(Arg.Any<string>(), Arg.Any<string>())
                .Returns(callInfo => callInfo.ArgAt<string>(0).Replace("protected::", string.Empty, StringComparison.Ordinal));
            secretProtectionCodec.IsProtected(Arg.Any<string>())
                .Returns(callInfo => callInfo.ArgAt<string>(0).StartsWith("protected::", StringComparison.Ordinal));

            services.AddSingleton<IJwtTokenService, JwtTokenService>();
            services.AddSingleton<IPasswordHashService, PasswordHashService>();
            services.AddSingleton(secretProtectionCodec);
            services.AddDbContext<MeisterProPRDbContext>(options => options.UseInMemoryDatabase(dbName, dbRoot));

            services.RemoveAll<ILicensingCapabilityService>();
            services.AddSingleton<ILicensingCapabilityService>(this._licensingCapabilityService);

            services.AddScoped<IUserRepository, AppUserRepository>();
            services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
            services.AddScoped<IUserPatRepository, UserPatRepository>();
            services.AddScoped<IClientAdminService, ClientAdminService>();
            services.AddScoped<ITenantAdminService, TenantAdminService>();
            services.AddScoped<ITenantMembershipService, TenantMembershipService>();
            services.AddScoped<ITenantSsoProviderService, TenantSsoProviderService>();
            services.AddScoped<ISessionFactory, SessionFactory>();
            services.AddHttpClient("TenantSsoAuth")
                .ConfigurePrimaryHttpMessageHandler(() => new StubHttpMessageHandler(this.DequeueExternalAuthResponse));

            services.AddSingleton(Substitute.For<IPullRequestFetcher>());
            services.AddSingleton(Substitute.For<IAdoCommentPoster>());
            services.AddSingleton(Substitute.For<IAssignedReviewDiscoveryService>());
            services.AddSingleton(Substitute.For<IPrStatusFetcher>());
            services.AddSingleton(Substitute.For<IThreadMemoryService>());

            var crawlRepo = Substitute.For<ICrawlConfigurationRepository>();
            crawlRepo.GetAllActiveAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([]));
            services.AddSingleton(crawlRepo);

            services.AddSingleton(Substitute.For<IJobRepository>());
            services.AddScoped<ITenantAuthService, TenantAuthService>();
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        dbContext.Database.EnsureCreated();
        EnsureSystemTenantSeeded(dbContext);

        return host;
    }

    private static void EnsureSystemTenantSeeded(MeisterProPRDbContext dbContext)
    {
        if (dbContext.Tenants.Any(tenant => tenant.Id == TenantCatalog.SystemTenantId))
        {
            return;
        }

        dbContext.Tenants.Add(
            new TenantRecord
            {
                Id = TenantCatalog.SystemTenantId,
                Slug = TenantCatalog.SystemTenantSlug,
                DisplayName = TenantCatalog.SystemTenantDisplayName,
                IsActive = TenantCatalog.SystemTenantIsActive,
                LocalLoginEnabled = TenantCatalog.SystemTenantLocalLoginEnabled,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        dbContext.SaveChanges();
    }

    private HttpResponseMessage DequeueExternalAuthResponse(HttpRequestMessage request)
    {
        lock (this._externalAuthResponsesLock)
        {
            if (this._externalAuthResponses.Count == 0)
            {
                throw new InvalidOperationException($"No tenant SSO auth response queued for {request.Method} {request.RequestUri}.");
            }

            return this._externalAuthResponses.Dequeue()(request);
        }
    }

    private sealed class TestLicensingCapabilityService : ILicensingCapabilityService
    {
        private readonly Dictionary<string, CapabilitySnapshot> _capabilities = new(StringComparer.OrdinalIgnoreCase);
        private readonly StaticPremiumCapabilityCatalog _catalog = new();
        private readonly object _sync = new();
        private InstallationEdition _edition = InstallationEdition.Commercial;

        public Task<LicensingSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default)
        {
            lock (this._sync)
            {
                return Task.FromResult(
                    new LicensingSummaryDto(
                        this._edition,
                        this._edition == InstallationEdition.Commercial ? DateTimeOffset.UtcNow : null,
                        this._capabilities.Values
                            .Select(capability => new PremiumCapabilityDto(
                                capability.Key,
                                capability.DisplayName,
                                capability.RequiresCommercial,
                                capability.DefaultWhenCommercial,
                                capability.OverrideState,
                                capability.IsAvailable,
                                capability.Message))
                            .ToList()
                            .AsReadOnly()));
            }
        }

        public async Task<AuthOptionsDto> GetAuthOptionsAsync(CancellationToken cancellationToken = default)
        {
            var summary = await this.GetSummaryAsync(cancellationToken);
            var signInMethods = new List<string> { "password" };
            if (summary.Capabilities.Any(capability =>
                    string.Equals(capability.Key, PremiumCapabilityKey.SsoAuthentication, StringComparison.OrdinalIgnoreCase)
                    && capability.IsAvailable))
            {
                signInMethods.Add("sso");
            }

            return new AuthOptionsDto(summary.Edition, signInMethods.AsReadOnly(), summary.Capabilities);
        }

        public Task<CapabilitySnapshot> GetCapabilityAsync(string capabilityKey, CancellationToken cancellationToken = default)
        {
            lock (this._sync)
            {
                if (this._capabilities.TryGetValue(capabilityKey, out var capability))
                {
                    return Task.FromResult(capability);
                }
            }

            throw new KeyNotFoundException($"Unknown premium capability '{capabilityKey}'.");
        }

        public async ValueTask<bool> IsEnabledAsync(string capabilityKey, CancellationToken cancellationToken = default)
        {
            return (await this.GetCapabilityAsync(capabilityKey, cancellationToken)).IsAvailable;
        }

        public Task<LicensingSummaryDto> UpdateAsync(
            InstallationEdition edition,
            IReadOnlyCollection<CapabilityOverrideMutation> capabilityOverrides,
            Guid? actorUserId,
            CancellationToken cancellationToken = default)
        {
            lock (this._sync)
            {
                this._edition = edition;
            }

            return this.GetSummaryAsync(cancellationToken);
        }

        public void Reset()
        {
            lock (this._sync)
            {
                this._edition = InstallationEdition.Commercial;
                this._capabilities.Clear();

                foreach (var definition in this._catalog.GetAll())
                {
                    this._capabilities[definition.Key] = new CapabilitySnapshot(
                        definition.Key,
                        definition.DisplayName,
                        definition.RequiresCommercial,
                        definition.DefaultWhenCommercial,
                        PremiumCapabilityOverrideState.Default,
                        true,
                        null);
                }
            }
        }

        public void SetCapabilityAvailability(string capabilityKey, bool isAvailable, string? message = null)
        {
            var definition = this._catalog.Get(capabilityKey)
                             ?? throw new KeyNotFoundException($"Unknown premium capability '{capabilityKey}'.");

            lock (this._sync)
            {
                this._capabilities[capabilityKey] = new CapabilitySnapshot(
                    definition.Key,
                    definition.DisplayName,
                    definition.RequiresCommercial,
                    definition.DefaultWhenCommercial,
                    PremiumCapabilityOverrideState.Default,
                    isAvailable,
                    isAvailable ? null : message ?? definition.CommercialRequiredMessage);
            }
        }

        public void SetEdition(InstallationEdition edition)
        {
            lock (this._sync)
            {
                this._edition = edition;
            }
        }
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = handler(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
