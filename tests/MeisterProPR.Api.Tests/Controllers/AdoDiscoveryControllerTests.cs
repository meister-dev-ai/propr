// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Auth;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
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

namespace MeisterProPR.Api.Tests.Controllers;

public sealed class AdoDiscoveryControllerTests(AdoDiscoveryControllerTests.AdoDiscoveryApiFactory factory)
    : IClassFixture<AdoDiscoveryControllerTests.AdoDiscoveryApiFactory>
{
    [Fact]
    public async Task GetProjects_ClientUserForAssignedClient_Returns200WithProjectOptions()
    {
        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/clients/{factory.ClientId}/ado/discovery/projects?organizationScopeId={factory.OrganizationScopeId}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientUserToken());

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Single(body.EnumerateArray());
        var project = body[0];
        Assert.Equal(factory.OrganizationScopeId, project.GetProperty("organizationScopeId").GetGuid());
        Assert.Equal("project-1", project.GetProperty("projectId").GetString());
        Assert.Equal("Project One", project.GetProperty("projectName").GetString());
    }

    [Fact]
    public async Task GetProjects_WithoutCredentials_Returns401()
    {
        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/clients/{factory.ClientId}/ado/discovery/projects?organizationScopeId={factory.OrganizationScopeId}");

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetSources_InvalidSourceKind_Returns400()
    {
        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/clients/{factory.ClientId}/ado/discovery/sources?organizationScopeId={factory.OrganizationScopeId}&projectId=project-1&sourceKind=not-real");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientUserToken());

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetBranches_UserWithoutClientAccess_Returns403()
    {
        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/clients/{factory.ClientId}/ado/discovery/branches?organizationScopeId={factory.OrganizationScopeId}&projectId=project-1&sourceKind=repository&canonicalSourceProvider=azureDevOps&canonicalSourceValue=repo-1");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateUnassignedUserToken());

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetBranches_ClientUserForAssignedClient_Returns200WithBranchOptions()
    {
        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/clients/{factory.ClientId}/ado/discovery/branches?organizationScopeId={factory.OrganizationScopeId}&projectId=project-1&sourceKind=repository&canonicalSourceProvider=azureDevOps&canonicalSourceValue=repo-1");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientUserToken());

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Single(body.EnumerateArray());
        var branch = body[0];
        Assert.Equal("main", branch.GetProperty("branchName").GetString());
        Assert.True(branch.GetProperty("isDefault").GetBoolean());
    }

    [Fact]
    public async Task GetCrawlFilters_ClientUserForAssignedClient_Returns200WithFilterOptions()
    {
        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/clients/{factory.ClientId}/ado/discovery/crawl-filters?organizationScopeId={factory.OrganizationScopeId}&projectId=project-1");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientUserToken());

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Single(body.EnumerateArray());
        var crawlFilter = body[0];
        Assert.Equal("Repository One", crawlFilter.GetProperty("displayName").GetString());
        var canonicalSourceRef = crawlFilter.GetProperty("canonicalSourceRef");
        Assert.Equal("azureDevOps", canonicalSourceRef.GetProperty("provider").GetString());
        Assert.Equal("repo-1", canonicalSourceRef.GetProperty("value").GetString());
        Assert.Single(crawlFilter.GetProperty("branchSuggestions").EnumerateArray());
    }

    [Fact]
    public async Task GetProjects_MissingOrganizationScope_Returns404WithError()
    {
        var missingScopeId = Guid.NewGuid();
        factory.DiscoveryService.ListProjectsAsync(factory.ClientId, missingScopeId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<AdoProjectOptionDto>>(
                new KeyNotFoundException($"Organization scope {missingScopeId} was not found for client {factory.ClientId}.")));

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/clients/{factory.ClientId}/ado/discovery/projects?organizationScopeId={missingScopeId}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientUserToken());

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Contains("not found", body.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetCrawlFilters_DisabledOrganizationScope_Returns400WithError()
    {
        var disabledScopeId = Guid.NewGuid();
        factory.DiscoveryService.ListCrawlFiltersAsync(factory.ClientId, disabledScopeId, "project-1", Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<IReadOnlyList<AdoCrawlFilterOptionDto>>(
                new InvalidOperationException("The selected organization scope is disabled.")));

        var httpClient = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/clients/{factory.ClientId}/ado/discovery/crawl-filters?organizationScopeId={disabledScopeId}&projectId=project-1");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientUserToken());

        var response = await httpClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Contains("disabled", body.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    public sealed class AdoDiscoveryApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-ado-discovery-jwt-secret-32ch";

        private readonly string _dbName = $"TestDb_AdoDiscovery_{Guid.NewGuid()}";
        private readonly InMemoryDatabaseRoot _dbRoot = new();

        public Guid ClientId { get; } = Guid.NewGuid();
        public Guid OrganizationScopeId { get; } = Guid.NewGuid();
        public Guid ClientUserId { get; } = Guid.NewGuid();
        public Guid UnassignedUserId { get; } = Guid.NewGuid();
        public IAdoDiscoveryService DiscoveryService { get; } = Substitute.For<IAdoDiscoveryService>();

        public string GenerateClientUserToken() => this.GenerateToken(this.ClientUserId, AppUserRole.User);

        public string GenerateUnassignedUserToken() => this.GenerateToken(this.UnassignedUserId, AppUserRole.User);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);

            var dbName = this._dbName;
            var dbRoot = this._dbRoot;
            var clientId = this.ClientId;
            var clientUserId = this.ClientUserId;
            var unassignedUserId = this.UnassignedUserId;
            var organizationScopeId = this.OrganizationScopeId;

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedService>();

                services.AddSingleton<IJwtTokenService, JwtTokenService>();
                services.AddDbContext<MeisterProPRDbContext>(options =>
                    options.UseInMemoryDatabase(dbName, dbRoot));
                services.AddDbContextFactory<MeisterProPRDbContext>(options =>
                    options.UseInMemoryDatabase(dbName, dbRoot));
                services.AddScoped<IClientAdminService, ClientAdminService>();
                services.AddScoped<IClientAdoOrganizationScopeRepository, ClientAdoOrganizationScopeRepository>();

                services.AddSingleton(Substitute.For<IAdoTokenValidator>());
                services.AddSingleton(Substitute.For<IPullRequestFetcher>());
                services.AddSingleton(Substitute.For<IAdoCommentPoster>());
                services.AddSingleton(Substitute.For<IAssignedPrFetcher>());
                services.AddSingleton(Substitute.For<IClientRegistry>());

                var discoveryService = this.DiscoveryService;
                discoveryService.ListProjectsAsync(clientId, organizationScopeId, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<AdoProjectOptionDto>>([
                        new AdoProjectOptionDto(organizationScopeId, "project-1", "Project One"),
                    ]));
                discoveryService.ListSourcesAsync(clientId, organizationScopeId, "project-1", ProCursorSourceKind.Repository, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<AdoSourceOptionDto>>([
                        new AdoSourceOptionDto(
                            "Repository",
                            new CanonicalSourceReferenceDto("azureDevOps", "repo-1"),
                            "Repository One",
                            "main"),
                    ]));
                discoveryService.ListBranchesAsync(
                        clientId,
                        organizationScopeId,
                        "project-1",
                        ProCursorSourceKind.Repository,
                        Arg.Is<CanonicalSourceReferenceDto>(dto => dto.Provider == "azureDevOps" && dto.Value == "repo-1"),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<AdoBranchOptionDto>>([
                        new AdoBranchOptionDto("main", true),
                    ]));
                discoveryService.ListCrawlFiltersAsync(clientId, organizationScopeId, "project-1", Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<AdoCrawlFilterOptionDto>>([
                        new AdoCrawlFilterOptionDto(
                            new CanonicalSourceReferenceDto("azureDevOps", "repo-1"),
                            "Repository One",
                            [new AdoBranchOptionDto("main", true)]),
                    ]));
                services.AddSingleton(discoveryService);

                var userRepo = Substitute.For<IUserRepository>();
                userRepo.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AppUser?>(null));
                userRepo.GetUserClientRolesAsync(clientUserId, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>
                    {
                        { clientId, ClientRole.ClientUser },
                    }));
                userRepo.GetUserClientRolesAsync(unassignedUserId, Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>()));
                userRepo.GetUserClientRolesAsync(
                        Arg.Is<Guid>(id => id != clientUserId && id != unassignedUserId),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>()));
                services.AddSingleton(userRepo);

                var crawlRepo = Substitute.For<ICrawlConfigurationRepository>();
                crawlRepo.GetAllActiveAsync(Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([]));
                services.AddSingleton(crawlRepo);

                var adoCredentialRepository = Substitute.For<IClientAdoCredentialRepository>();
                adoCredentialRepository.GetByClientIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<ClientAdoCredentials?>(null));
                adoCredentialRepository.UpsertAsync(Arg.Any<Guid>(), Arg.Any<ClientAdoCredentials>(), Arg.Any<CancellationToken>())
                    .Returns(Task.CompletedTask);
                adoCredentialRepository.ClearAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.CompletedTask);
                services.AddSingleton(adoCredentialRepository);
                services.AddSingleton(Substitute.For<IJobRepository>());
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = base.CreateHost(builder);

            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.Clients.Add(new ClientRecord
            {
                Id = this.ClientId,
                DisplayName = "Discovery Client",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.SaveChanges();

            return host;
        }

        private string GenerateToken(Guid userId, AppUserRole role)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
            var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var token = new JwtSecurityToken(
                issuer: "meisterpropr",
                audience: "meisterpropr",
                claims:
                [
                    new Claim("sub", userId.ToString()),
                    new Claim("global_role", role.ToString()),
                ],
                expires: DateTime.UtcNow.AddHours(1),
                signingCredentials: credentials);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
