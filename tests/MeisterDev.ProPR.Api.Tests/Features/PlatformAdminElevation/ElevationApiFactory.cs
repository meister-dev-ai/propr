// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using MeisterDev.ProPR.Api.Features.IdentityAndAccess.Authentication;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Ports;
using MeisterDev.ProPR.Application.Features.Reviewing.Intake.Ports;
using MeisterDev.ProPR.Application.Features.TenantData;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Auth;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess.Persistence;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.Intake.Persistence;
using MeisterDev.ProPR.Infrastructure.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace MeisterDev.ProPR.Api.Tests.Features.PlatformAdminElevation;

/// <summary>
///     An API host on an in-memory database with the real job, user, membership and machine credential stores, so a
///     test seeds tenants, clients and reviews and reads them through the endpoints as the caller it names. The tenant
///     data lifecycle is a substitute, because the data request tests concern who may call the endpoints, not what the
///     lifecycle does.
/// </summary>
public sealed class ElevationApiFactory : WebApplicationFactory<Program>
{
    private const string TestJwtSecret = "test-elevation-api-jwt-secret-32-chars!";

    private readonly string _dbName = $"TestDb_Elevation_{Guid.NewGuid()}";
    private readonly InMemoryDatabaseRoot _dbRoot = new();

    /// <summary>
    ///     Whether the license of the host includes multi-tenancy. Every other capability is available. Set it before the
    ///     first request, because the host reads it when it starts.
    /// </summary>
    public bool MultiTenancyAvailable { get; init; } = true;

    /// <summary>The tenant data lifecycle the data request endpoints call.</summary>
    public ITenantDataLifecycle Lifecycle { get; } = Substitute.For<ITenantDataLifecycle>();

    /// <summary>The thread memory store, which answers an empty page for every client.</summary>
    public IThreadMemoryRepository ThreadMemory { get; } = CreateThreadMemory();

    /// <summary>Returns an HTTP client that sends a bearer token for the user.</summary>
    public HttpClient ClientFor(Guid userId, AppUserRole role)
    {
        var client = this.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", this.Token(userId, role));
        return client;
    }

    /// <summary>Returns an HTTP client that sends a tenant machine credential.</summary>
    public HttpClient ClientForMachine(string token)
    {
        var client = this.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Machine-Token", token);
        return client;
    }

    public string Token(Guid userId, AppUserRole role)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", userId.ToString()),
                new Claim("global_role", role.ToString()),
            ]),
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
            Issuer = "meisterpropr",
            Audience = "meisterpropr",
        };
        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    public async Task<Guid> SeedUserAsync(string username, AppUserRole role)
    {
        using var scope = this.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = $"{username}@example.test",
            NormalizedEmail = $"{username}@example.test".ToUpperInvariant(),
            GlobalRole = role,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await users.AddAsync(user);
        return user.Id;
    }

    public async Task<Guid> SeedTenantAsync(string displayName)
    {
        using var scope = this.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var tenant = new TenantRecord
        {
            Id = Guid.NewGuid(),
            Slug = $"tenant-{Guid.NewGuid():N}",
            DisplayName = displayName,
            IsActive = true,
            LocalLoginEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        return tenant.Id;
    }

    public async Task<Guid> SeedClientAsync(Guid tenantId, string displayName)
    {
        using var scope = this.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var client = new ClientRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            DisplayName = displayName,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client.Id;
    }

    public async Task SeedMembershipAsync(Guid tenantId, Guid userId, TenantRole role)
    {
        using var scope = this.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ITenantMembershipService>().UpsertAsync(tenantId, userId, role);
    }

    /// <summary>Seeds a completed review job of the client whose result carries <paramref name="summary" />.</summary>
    public async Task<Guid> SeedCompletedJobAsync(Guid clientId, string summary)
    {
        using var scope = this.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = new ReviewJob(Guid.NewGuid(), clientId, "https://dev.azure.com/org", "project", "repository", Random.Shared.Next(1, 100000), 1);
        await jobs.AddAsync(job);
        await jobs.TryTransitionAsync(job.Id, JobStatus.Pending, JobStatus.Processing);
        await jobs.SetResultAsync(job.Id, new ReviewResult(summary, []));
        return job.Id;
    }

    /// <summary>Seeds a failed review job of the client whose error message is <paramref name="error" />.</summary>
    public async Task<Guid> SeedFailedJobAsync(Guid clientId, string error)
    {
        using var scope = this.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var job = new ReviewJob(Guid.NewGuid(), clientId, "https://dev.azure.com/org", "project", "repository", Random.Shared.Next(1, 100000), 1);
        await jobs.AddAsync(job);
        await jobs.SetFailedAsync(job.Id, error);
        return job.Id;
    }

    public async Task<string> IssueMachineTokenAsync(Guid tenantId)
    {
        using var scope = this.Services.CreateScope();
        var credentials = scope.ServiceProvider.GetRequiredService<TenantMachineCredentialService>();
        var issued = await credentials.IssueAsync(tenantId, "hosted platform", null, Guid.NewGuid(), CancellationToken.None);
        return issued!.Token;
    }

    /// <summary>Moves the expiry of an elevation into the past, as if its duration had run out.</summary>
    public async Task ExpireAsync(Guid elevationId)
    {
        using var scope = this.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var elevation = await db.PlatformAdminElevations.SingleAsync(row => row.Id == elevationId);
        elevation.StartedAt = DateTimeOffset.UtcNow.AddHours(-2);
        elevation.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    public async Task<List<TenantAuditEntryRecord>> AuditEntriesAsync(Guid tenantId)
    {
        using var scope = this.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        return await db.TenantAuditEntries.AsNoTracking().Where(entry => entry.TenantId == tenantId).ToListAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
        builder.UseSetting("AI_ENDPOINT", "https://fake.openai.azure.com/");
        builder.UseSetting("AI_DEPLOYMENT", "gpt-4o");
        builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

        var dbName = this._dbName;
        var dbRoot = this._dbRoot;
        builder.ConfigureServices(services =>
        {
            var secretProtectionCodec = Substitute.For<ISecretProtectionCodec>();
            secretProtectionCodec.Protect(Arg.Any<string>(), Arg.Any<string>())
                .Returns(callInfo => $"protected::{callInfo.ArgAt<string>(0)}");
            secretProtectionCodec.Unprotect(Arg.Any<string>(), Arg.Any<string>())
                .Returns(callInfo => callInfo.ArgAt<string>(0).Replace("protected::", string.Empty, StringComparison.Ordinal));

            services.AddSingleton<IJwtTokenService, JwtTokenService>();
            services.AddSingleton<IPasswordHashService, PasswordHashService>();
            services.AddSingleton(secretProtectionCodec);
            services.AddDbContext<MeisterProPRDbContext>(options => options.UseInMemoryDatabase(dbName, dbRoot));
            services.AddDbContextFactory<MeisterProPRDbContext>(options => options.UseInMemoryDatabase(dbName, dbRoot));

            services.AddScoped<IUserRepository, AppUserRepository>();
            services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
            services.AddScoped<IUserPatRepository, UserPatRepository>();
            services.AddScoped<ITenantAdminService, TenantAdminService>();
            services.AddScoped<ITenantMembershipService, TenantMembershipService>();
            services.AddScoped<ITenantMemberClientAccessService, TenantMemberClientAccessService>();
            services.AddScoped<TenantMachineCredentialService>();

            services.RemoveAll<IJobRepository>();
            services.AddScoped<IJobRepository, JobRepository>();
            services.RemoveAll<IReviewJobIntakeStore>();
            services.AddScoped<IReviewJobIntakeStore>(sp => new EfReviewJobIntakeStore(sp.GetRequiredService<MeisterProPRDbContext>()));
            services.RemoveAll<ITenantDataLifecycle>();
            services.AddSingleton(this.Lifecycle);
            services.RemoveAll<IThreadMemoryRepository>();
            services.AddSingleton(this.ThreadMemory);
            services.RemoveAll<IReviewPrScanThreadStatusStore>();
            services.AddSingleton(Substitute.For<IReviewPrScanThreadStatusStore>());
            services.RemoveAll<IMemoryActivityLog>();
            services.AddSingleton(Substitute.For<IMemoryActivityLog>());

            // Every capability answers as available, multi-tenancy among them unless the test turns it off, so tenant
            // roles project onto the clients of a tenant other than the System tenant as they do in a licensed
            // installation.
            var multiTenancyAvailable = this.MultiTenancyAvailable;
            var licensing = Substitute.For<ILicensingCapabilityService>();
            licensing.IsEnabledAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => new ValueTask<bool>(IsAvailable(call.ArgAt<string>(0), multiTenancyAvailable)));
            licensing.GetCapabilityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => Capability(call.ArgAt<string>(0), multiTenancyAvailable));
            services.RemoveAll<ILicensingCapabilityService>();
            services.AddSingleton(licensing);

            var history = Substitute.For<ICustomerReviewHistoryReader>();
            history.GetAsync(Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<JobStatus?>(), Arg.Any<CancellationToken>())
                .Returns(new CustomerReviewHistory(0, 1, 25, []));
            services.RemoveAll<ICustomerReviewHistoryReader>();
            services.AddSingleton(history);
            services.AddScoped<ICustomerDashboardReader, EfCustomerDashboardReader>();

            services.AddSingleton(Substitute.For<IPullRequestFetcher>());
            services.AddSingleton(Substitute.For<IAdoCommentPoster>());
            services.AddSingleton(Substitute.For<IAssignedReviewDiscoveryService>());
            services.AddSingleton(Substitute.For<IPrStatusFetcher>());
            services.AddSingleton(Substitute.For<IThreadMemoryService>());
            var crawlRepo = Substitute.For<ICrawlConfigurationRepository>();
            crawlRepo.GetAllActiveAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([]));
            services.AddSingleton(crawlRepo);
            services.AddSingleton(Substitute.For<IWebhookConfigurationRepository>());
            services.AddSingleton(MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute());
            services.AddSingleton(Substitute.For<MeisterDev.ProPR.Application.Features.Crawling.Execution.Ports.IPullRequestSynchronizationService>());
        });
    }

    private static bool IsAvailable(string capabilityKey, bool multiTenancyAvailable)
    {
        return multiTenancyAvailable || !string.Equals(capabilityKey, PremiumCapabilityKey.MultiTenancy, StringComparison.Ordinal);
    }

    private static CapabilitySnapshot Capability(string capabilityKey, bool multiTenancyAvailable)
    {
        var isAvailable = IsAvailable(capabilityKey, multiTenancyAvailable);
        return new CapabilitySnapshot(
            capabilityKey,
            capabilityKey,
            true,
            PremiumCapabilityOverrideState.Default,
            isAvailable,
            isAvailable ? null : $"The license of this installation does not include '{capabilityKey}'.",
            isAvailable ? null : PremiumCapabilityUnavailableReason.NotInLicense);
    }

    private static IThreadMemoryRepository CreateThreadMemory()
    {
        var threadMemory = Substitute.For<IThreadMemoryRepository>();
        threadMemory.GetPagedAsync(
                Arg.Any<Guid>(),
                Arg.Any<string?>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<MemorySource?>(),
                Arg.Any<string?>(),
                Arg.Any<int?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => new PagedResult<ThreadMemoryRecord>([], 0, call.ArgAt<int>(2), call.ArgAt<int>(3)));
        return threadMemory;
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        db.Database.EnsureCreated();
        if (!db.Tenants.Any(tenant => tenant.Id == TenantCatalog.SystemTenantId))
        {
            db.Tenants.Add(
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
            db.SaveChanges();
        }

        return host;
    }
}
