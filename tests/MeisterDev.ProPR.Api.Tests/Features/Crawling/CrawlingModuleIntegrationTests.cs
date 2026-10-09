// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MeisterDev.ProPR.Api.Tests.Controllers;
using MeisterDev.ProPR.Api.Features.IdentityAndAccess.Authentication;
using MeisterDev.ProPR.Application.Features.Crawling.Configuration;
using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess.Authentication.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Identity;

namespace MeisterDev.ProPR.Api.Tests.Features.Crawling;

public sealed class CrawlingConfigurationModuleIntegrationTests(AdminCrawlConfigsControllerTests.AdminCrawlConfigsApiFactory factory)
    : IClassFixture<AdminCrawlConfigsControllerTests.AdminCrawlConfigsApiFactory>
{
    [Fact]
    public async Task CreateManualCrawlConfiguration_WithExplicitProviderAndSavedScope_ReturnsSelectedMetadata()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/crawl-configurations");
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

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(factory.GuidedOrganizationScopeId, body.GetProperty("organizationScopeId").GetGuid());
        Assert.Equal(factory.GuidedProCursorSourceId, body.GetProperty("proCursorSourceIds")[0].GetGuid());
    }
}

public sealed class CrawlingOverviewModuleIntegrationTests
{
    [Fact]
    public async Task Overview_ForeignClientUserIsRejectedBeforeServiceInvocation()
    {
        using var factory = new AdminCrawlConfigsControllerTests.AdminCrawlConfigsApiFactory();
        var overview = Overview();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton(overview);
            services.AddSingleton(Substitute.For<IClientScmConnectionRepository>());
        }));
        using var http = host.CreateClient();
        using var request = Request(Guid.NewGuid());
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateUserToken(factory.TestUserId));
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(overview.ReceivedCalls());
    }

    [Fact]
    public async Task Overview_ClientUserCapabilityRevocationPreventsAnotherServiceInvocation()
    {
        using var factory = new AdminCrawlConfigsControllerTests.AdminCrawlConfigsApiFactory();
        var overview = Overview();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton(overview);
            services.AddSingleton(Substitute.For<IClientScmConnectionRepository>());
        }));
        using var http = host.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateUserToken(factory.TestUserId));
        using var allowed = await http.SendAsync(Request(factory.TestClientId));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        factory.SetCrawlConfigsCapabilityAvailability(false);
        using var denied = await http.SendAsync(Request(factory.TestClientId));
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
        Assert.Single(overview.ReceivedCalls());
    }

    [Fact]
    public async Task Overview_TenantMembershipRevocationPreventsAnotherServiceInvocation()
    {
        using var factory = new AdminCrawlConfigsControllerTests.AdminCrawlConfigsApiFactory();
        await using var db = Database();
        var tenant = Guid.NewGuid();
        db.Clients.Add(new ClientRecord { Id = factory.TestClientId, TenantId = tenant, DisplayName = "Client", IsActive = true });
        await db.SaveChangesAsync();
        var user = new AppUser { Id = factory.TestUserId, Username = "overview-user", GlobalRole = AppUserRole.User, IsActive = true };
        user.ClientAssignments.Add(new UserClientRole { ClientId = factory.TestClientId, UserId = user.Id, Role = ClientRole.ClientUser });
        user.TenantMemberships.Add(new TenantMembership { Id = Guid.NewGuid(), TenantId = tenant, UserId = user.Id, Role = TenantRole.TenantUser });
        var users = Substitute.For<IUserRepository>();
        users.GetByIdWithAssignmentsAsync(user.Id, Arg.Any<CancellationToken>()).Returns(_ => user);
        var overview = Overview();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton(db);
            services.AddSingleton(users);
            services.AddSingleton(overview);
            services.AddSingleton(Substitute.For<IClientScmConnectionRepository>());
            factory.LicensingCapabilityService.IsEnabledAsync(PremiumCapabilityKey.MultiTenancy, Arg.Any<CancellationToken>())
                .Returns(new ValueTask<bool>(true));
        }));
        using var http = host.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateUserToken(user.Id));
        using var allowed = await http.SendAsync(Request(factory.TestClientId));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        user.TenantMemberships.Clear();
        using var denied = await http.SendAsync(Request(factory.TestClientId));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Single(overview.ReceivedCalls());
    }

    [Fact]
    public async Task Overview_MachineCallerRechecksTenantOwnershipAndCapabilityBeforeServiceInvocation()
    {
        using var factory = new AdminCrawlConfigsControllerTests.AdminCrawlConfigsApiFactory();
        await using var db = Database();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        db.Tenants.AddRange(
            new TenantRecord { Id = tenantA, Slug = "overview-a", DisplayName = "A", IsActive = true },
            new TenantRecord { Id = tenantB, Slug = "overview-b", DisplayName = "B", IsActive = true });
        var client = new ClientRecord { Id = factory.TestClientId, TenantId = tenantA, DisplayName = "Client", IsActive = true };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        var machine = new TenantMachineCredentialService(new EfTenantMachineCredentialStore(db));
        var credentialA = await machine.IssueAsync(tenantA, "Fixture A", null, Guid.NewGuid(), default);
        var credentialB = await machine.IssueAsync(tenantB, "Fixture B", null, Guid.NewGuid(), default);
        Assert.NotNull(credentialA);
        Assert.NotNull(credentialB);
        var overview = Overview();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.AddSingleton(db);
            services.AddSingleton(machine);
            services.AddSingleton(overview);
            services.AddSingleton(Substitute.For<IClientScmConnectionRepository>());
        }));
        using var http = host.CreateClient();
        using var foreign = Request(client.Id);
        foreign.Headers.Add("X-Tenant-Machine-Token", credentialB.Token);
        using var foreignResponse = await http.SendAsync(foreign);
        Assert.Equal(HttpStatusCode.Forbidden, foreignResponse.StatusCode);
        Assert.Empty(overview.ReceivedCalls());
        using var own = Request(client.Id);
        own.Headers.Add("X-Tenant-Machine-Token", credentialA.Token);
        using var ownResponse = await http.SendAsync(own);
        Assert.Equal(HttpStatusCode.OK, ownResponse.StatusCode);
        Assert.Single(overview.ReceivedCalls());
        client.TenantId = tenantB;
        await db.SaveChangesAsync();
        using var moved = Request(client.Id);
        moved.Headers.Add("X-Tenant-Machine-Token", credentialA.Token);
        using var movedResponse = await http.SendAsync(moved);
        Assert.Equal(HttpStatusCode.Forbidden, movedResponse.StatusCode);
        Assert.Single(overview.ReceivedCalls());
        using var newOwner = Request(client.Id);
        newOwner.Headers.Add("X-Tenant-Machine-Token", credentialB.Token);
        using var newOwnerResponse = await http.SendAsync(newOwner);
        Assert.Equal(HttpStatusCode.OK, newOwnerResponse.StatusCode);
        Assert.Equal(2, overview.ReceivedCalls().Count());
        factory.SetCrawlConfigsCapabilityAvailability(false);
        using var unavailable = Request(client.Id);
        unavailable.Headers.Add("X-Tenant-Machine-Token", credentialB.Token);
        using var unavailableResponse = await http.SendAsync(unavailable);
        Assert.Equal(HttpStatusCode.Conflict, unavailableResponse.StatusCode);
        Assert.Equal(2, overview.ReceivedCalls().Count());
    }

    private static MeisterProPRDbContext Database() => new(
        new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase("overview-" + Guid.NewGuid()).Options);

    private static HttpRequestMessage Request(Guid client) => new(HttpMethod.Post, $"/clients/{client}/review-targets/overview")
    {
        Content = JsonContent.Create(new ClientPullRequestOverviewRequest([], "binding"))
    };

    private static IClientPullRequestOverviewService Overview()
    {
        var overview = Substitute.For<IClientPullRequestOverviewService>();
        var now = DateTimeOffset.UtcNow;
        overview.ReadAsync(Arg.Any<Guid>(), Arg.Any<ClientPullRequestOverviewRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ClientPullRequestOverviewPage([], [], "fixture-cursor", null, 1, 25, 0, 0, 0, now, now.AddMinutes(15), false, null));
        return overview;
    }
}

public sealed class CrawlingDiscoveryModuleIntegrationTests(ConnectionDiscoveryIntegrationTests.ConnectionDiscoveryApiFactory factory)
    : IClassFixture<ConnectionDiscoveryIntegrationTests.ConnectionDiscoveryApiFactory>
{
    [Fact]
    public async Task GetCrawlFilters_ClientUserForAssignedClient_ReturnsScopedDiscoveryOptions()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/clients/{factory.ClientId}/connections/{factory.ConnectionId}/discovery/filters?purpose=webhook&scopeKey={factory.OrganizationScopeId}&projectId=project-1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateClientUserToken());

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Single(body.EnumerateArray());
        Assert.Equal("Repository One", body[0].GetProperty("displayName").GetString());
    }
}

public sealed class CrawlingIdentityResolutionModuleIntegrationTests(IdentitiesControllerTests.IdentitiesApiFactory factory)
    : IClassFixture<IdentitiesControllerTests.IdentitiesApiFactory>
{
    [Fact]
    public async Task ResolveIdentity_ClientAdministrator_ReturnsResolvedIdentity()
    {
        var http = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/identities/resolve?clientId={factory.AssignedClientId}&orgUrl=https://dev.azure.com/org&displayName=Reviewer");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains("Reviewer", payload, StringComparison.Ordinal);
    }
}
