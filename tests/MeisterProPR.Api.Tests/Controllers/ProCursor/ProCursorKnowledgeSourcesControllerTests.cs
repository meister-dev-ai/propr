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
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace MeisterProPR.Api.Tests.Controllers.ProCursor;

public sealed class ProCursorKnowledgeSourcesControllerTests(ProCursorKnowledgeSourcesControllerTests.ProCursorApiFactory factory)
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
    public async Task CreateSource_ClientAdministrator_Returns201AndPersistsSource()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/admin/clients/{factory.ClientId}/procursor/sources");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());
        request.Content = JsonContent.Create(
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

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        Assert.Equal(1, await db.ProCursorKnowledgeSources.CountAsync(source => source.ClientId == factory.ClientId));
        Assert.Equal(1, await db.ProCursorTrackedBranches.CountAsync());
    }

    [Fact]
    public async Task CreateSource_ClientAdministrator_Returns201AndPersistsGuidedRepositorySource()
    {
        var organizationScopeId = await factory.SeedOrganizationScopeAsync(
            "https://dev.azure.com/test-org",
            "Test Org");
        var canonicalSourceRef = new CanonicalSourceReferenceDto("azureDevOps", "repo-guided");

        factory.AdoDiscoveryService
            .ListSourcesAsync(
                factory.ClientId,
                organizationScopeId,
                "project-a",
                ProCursorSourceKind.Repository,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AdoSourceOptionDto>>([new AdoSourceOptionDto("repository", canonicalSourceRef, "Contoso.Api", "main")]));
        factory.AdoDiscoveryService
            .ListBranchesAsync(
                factory.ClientId,
                organizationScopeId,
                "project-a",
                ProCursorSourceKind.Repository,
                Arg.Is<CanonicalSourceReferenceDto>(value =>
                    value.Provider == canonicalSourceRef.Provider &&
                    value.Value == canonicalSourceRef.Value),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AdoBranchOptionDto>>([new AdoBranchOptionDto("main", true)]));

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/admin/clients/{factory.ClientId}/procursor/sources");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());
        request.Content = JsonContent.Create(
            new
            {
                displayName = "Knowledge Repo",
                sourceKind = "repository",
                organizationScopeId,
                providerProjectKey = "project-a",
                canonicalSourceRef = new { provider = canonicalSourceRef.Provider, value = canonicalSourceRef.Value },
                sourceDisplayName = "Contoso.Api",
                defaultBranch = "main",
                symbolMode = "auto",
                trackedBranches = new[]
                {
                    new { branchName = "main", refreshTriggerMode = "branchUpdate", miniIndexEnabled = true },
                },
            });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var source =
            await db.ProCursorKnowledgeSources.SingleAsync(candidate => candidate.ClientId == factory.ClientId);
        Assert.Equal(organizationScopeId, source.OrganizationScopeId);
        Assert.Equal("https://dev.azure.com/test-org", source.ProviderScopePath);
        Assert.Equal("project-a", source.ProviderProjectKey);
        Assert.Equal("repo-guided", source.RepositoryId);
        Assert.Equal("azureDevOps", source.CanonicalSourceProvider);
        Assert.Equal("repo-guided", source.CanonicalSourceValue);
        Assert.Equal("Contoso.Api", source.SourceDisplayName);
    }

    [Fact]
    public async Task CreateSource_ClientAdministrator_Returns201AndPersistsGuidedWikiSource()
    {
        var organizationScopeId = await factory.SeedOrganizationScopeAsync(
            "https://dev.azure.com/test-org",
            "Test Org");
        var canonicalSourceRef = new CanonicalSourceReferenceDto("azureDevOps", "wiki-guided");

        factory.AdoDiscoveryService
            .ListSourcesAsync(
                factory.ClientId,
                organizationScopeId,
                "project-a",
                ProCursorSourceKind.AdoWiki,
                Arg.Any<CancellationToken>())
            .Returns(
                Task.FromResult<IReadOnlyList<AdoSourceOptionDto>>([new AdoSourceOptionDto("adoWiki", canonicalSourceRef, "Engineering Wiki", "wikiMain")]));
        factory.AdoDiscoveryService
            .ListBranchesAsync(
                factory.ClientId,
                organizationScopeId,
                "project-a",
                ProCursorSourceKind.AdoWiki,
                Arg.Is<CanonicalSourceReferenceDto>(value =>
                    value.Provider == canonicalSourceRef.Provider &&
                    value.Value == canonicalSourceRef.Value),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AdoBranchOptionDto>>([new AdoBranchOptionDto("wikiMain", true)]));

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/admin/clients/{factory.ClientId}/procursor/sources");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());
        request.Content = JsonContent.Create(
            new
            {
                displayName = "Knowledge Wiki",
                sourceKind = "adoWiki",
                organizationScopeId,
                providerProjectKey = "project-a",
                canonicalSourceRef = new { provider = canonicalSourceRef.Provider, value = canonicalSourceRef.Value },
                sourceDisplayName = "Engineering Wiki",
                defaultBranch = "wikiMain",
                symbolMode = "auto",
                trackedBranches = new[]
                {
                    new { branchName = "wikiMain", refreshTriggerMode = "branchUpdate", miniIndexEnabled = true },
                },
            });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        var source =
            await db.ProCursorKnowledgeSources.SingleAsync(candidate => candidate.ClientId == factory.ClientId);
        Assert.Equal(ProCursorSourceKind.AdoWiki, source.SourceKind);
        Assert.Equal(organizationScopeId, source.OrganizationScopeId);
        Assert.Equal("wiki-guided", source.RepositoryId);
        Assert.Equal("azureDevOps", source.CanonicalSourceProvider);
        Assert.Equal("wiki-guided", source.CanonicalSourceValue);
        Assert.Equal("Engineering Wiki", source.SourceDisplayName);
    }

    [Fact]
    public async Task CreateSource_ClientAdministrator_Returns409WhenGuidedSourceSelectionIsStale()
    {
        var organizationScopeId = await factory.SeedOrganizationScopeAsync(
            "https://dev.azure.com/test-org",
            "Test Org");
        var canonicalSourceRef = new CanonicalSourceReferenceDto("azureDevOps", "repo-stale");

        factory.AdoDiscoveryService
            .ListSourcesAsync(
                factory.ClientId,
                organizationScopeId,
                "project-a",
                ProCursorSourceKind.Repository,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AdoSourceOptionDto>>([]));

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/admin/clients/{factory.ClientId}/procursor/sources");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());
        request.Content = JsonContent.Create(
            new
            {
                displayName = "Knowledge Repo",
                sourceKind = "repository",
                organizationScopeId,
                providerProjectKey = "project-a",
                canonicalSourceRef = new { provider = canonicalSourceRef.Provider, value = canonicalSourceRef.Value },
                sourceDisplayName = "Missing Repo",
                defaultBranch = "main",
                symbolMode = "auto",
                trackedBranches = new[]
                {
                    new { branchName = "main", refreshTriggerMode = "branchUpdate", miniIndexEnabled = true },
                },
            });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var payload = await response.Content.ReadAsStringAsync();
        Assert.Contains("selected source", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateSource_ClientAdministrator_Returns404WhenOrganizationScopeBelongsToDifferentClient()
    {
        var otherClientScopeId = await factory.SeedOrganizationScopeAsync(
            "https://dev.azure.com/other-org",
            "Other Org",
            clientId: factory.OtherClientId);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/admin/clients/{factory.ClientId}/procursor/sources");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());
        request.Content = JsonContent.Create(
            new
            {
                displayName = "Knowledge Repo",
                sourceKind = "repository",
                organizationScopeId = otherClientScopeId,
                providerProjectKey = "project-a",
                canonicalSourceRef = new { provider = "azureDevOps", value = "repo-guided" },
                sourceDisplayName = "Repo Guided",
                defaultBranch = "main",
                symbolMode = "auto",
                trackedBranches = new[]
                {
                    new { branchName = "main", refreshTriggerMode = "branchUpdate", miniIndexEnabled = true },
                },
            });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task CreateSource_ClientUser_Returns403()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/admin/clients/{factory.ClientId}/procursor/sources");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateClientUserToken());
        request.Content = JsonContent.Create(
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

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ListSources_ClientUser_ReturnsRepositoryAndWikiSources()
    {
        await factory.SeedSourceAsync("Repo Source", repositoryId: "repo-source", defaultBranch: "main");
        await factory.SeedSourceAsync(
            "Wiki Source",
            ProCursorSourceKind.AdoWiki,
            "wiki-source",
            "wikiMaster");

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/clients/{factory.ClientId}/procursor/sources");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateClientUserToken());

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Equal(2, body.GetArrayLength());
        var displayNames = body.EnumerateArray()
            .Select(item => item.GetProperty("displayName").GetString())
            .ToArray();
        Assert.Contains("Repo Source", displayNames);
        Assert.Contains("Wiki Source", displayNames);
    }

    [Fact]
    public async Task QueueRefresh_ClientAdministrator_Returns202AndCreatesPendingJob()
    {
        var sourceId = await factory.SeedSourceAsync();
        var branchId = await factory.GetOnlyTrackedBranchIdAsync(sourceId);

        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/admin/clients/{factory.ClientId}/procursor/sources/{sourceId}/refresh");
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());
        request.Content = JsonContent.Create(new { trackedBranchId = branchId, jobKind = "refresh" });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
        Assert.Equal(1, await db.ProCursorIndexJobs.CountAsync(job => job.KnowledgeSourceId == sourceId));
    }

    [Fact]
    public async Task TrackedBranchEndpoints_AddUpdateRemoveBranch()
    {
        var sourceId = await factory.SeedSourceAsync();
        var client = factory.CreateClient();

        using var addRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/admin/clients/{factory.ClientId}/procursor/sources/{sourceId}/branches");
        addRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());
        addRequest.Content = JsonContent.Create(new { branchName = "release/1.0", refreshTriggerMode = "manual", miniIndexEnabled = false });
        var addResponse = await client.SendAsync(addRequest);
        Assert.Equal(HttpStatusCode.Created, addResponse.StatusCode);

        var createdBranch = JsonDocument.Parse(await addResponse.Content.ReadAsStringAsync()).RootElement;
        var branchId = createdBranch.GetProperty("branchId").GetGuid();

        using var updateRequest = new HttpRequestMessage(
            HttpMethod.Put,
            $"/admin/clients/{factory.ClientId}/procursor/sources/{sourceId}/branches/{branchId}");
        updateRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());
        updateRequest.Content = JsonContent.Create(new { refreshTriggerMode = "branchUpdate", miniIndexEnabled = true, isEnabled = true });
        var updateResponse = await client.SendAsync(updateRequest);
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);

        using var listRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/admin/clients/{factory.ClientId}/procursor/sources/{sourceId}/branches");
        listRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", factory.GenerateClientUserToken());
        var listResponse = await client.SendAsync(listRequest);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        using var deleteRequest = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/admin/clients/{factory.ClientId}/procursor/sources/{sourceId}/branches/{branchId}");
        deleteRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            factory.GenerateClientAdministratorToken());
        var deleteResponse = await client.SendAsync(deleteRequest);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
    }

    [Fact]
    public async Task OpenApi_ContainsProCursorPaths()
    {
        var openApiPath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "..",
                "openapi.json"));
        var content = await File.ReadAllTextAsync(openApiPath);

        Assert.Contains("/admin/clients/{clientId}/procursor/sources", content, StringComparison.Ordinal);
        Assert.Contains(
            "/admin/clients/{clientId}/procursor/sources/{sourceId}/branches",
            content,
            StringComparison.Ordinal);
        Assert.Contains(
            "/admin/clients/{clientId}/procursor/sources/{sourceId}/refresh",
            content,
            StringComparison.Ordinal);
    }

    public sealed class ProCursorApiFactory : WebApplicationFactory<Program>
    {
        private const string TestJwtSecret = "test-procursor-api-jwt-secret-32char";

        private readonly string _dbName = $"TestDb_ProCursor_{Guid.NewGuid()}";
        private readonly InMemoryDatabaseRoot _dbRoot = new();

        public Guid ClientId { get; } = Guid.NewGuid();
        public Guid OtherClientId { get; } = Guid.NewGuid();
        public Guid ClientAdministratorUserId { get; } = Guid.NewGuid();
        public Guid ClientUserId { get; } = Guid.NewGuid();

        public IProviderAdminDiscoveryService AdoDiscoveryService { get; } =
            Substitute.For<IProviderAdminDiscoveryService>();

        public string GenerateClientAdministratorToken()
        {
            return this.GenerateToken(this.ClientAdministratorUserId, AppUserRole.User);
        }

        public string GenerateClientUserToken()
        {
            return this.GenerateToken(this.ClientUserId, AppUserRole.User);
        }

        public async Task<Guid> SeedSourceAsync(
            string displayName = "Seeded Source",
            ProCursorSourceKind sourceKind = ProCursorSourceKind.Repository,
            string repositoryId = "seed-repo",
            string defaultBranch = "main")
        {
            using var scope = this.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();

            var source = new ProCursorKnowledgeSource(
                Guid.NewGuid(),
                this.ClientId,
                displayName,
                sourceKind,
                "https://dev.azure.com/test-org",
                "seed-project",
                repositoryId,
                defaultBranch,
                null,
                true,
                "auto");
            source.AddTrackedBranch(Guid.NewGuid(), defaultBranch, ProCursorRefreshTriggerMode.BranchUpdate, true);

            db.ProCursorKnowledgeSources.Add(source);
            await db.SaveChangesAsync();
            return source.Id;
        }

        public async Task<Guid> SeedOrganizationScopeAsync(
            string organizationUrl,
            string? displayName = null,
            bool isEnabled = true,
            Guid? clientId = null)
        {
            using var scope = this.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            var resolvedClientId = clientId ?? this.ClientId;

            var connectionId = Guid.NewGuid();
            db.ClientScmConnections.Add(
                new ClientScmConnectionRecord
                {
                    Id = connectionId,
                    ClientId = resolvedClientId,
                    Provider = ScmProvider.AzureDevOps,
                    HostBaseUrl = "https://dev.azure.com",
                    AuthenticationKind = ScmAuthenticationKind.OAuthClientCredentials,
                    OAuthTenantId = "contoso.onmicrosoft.com",
                    OAuthClientId = "11111111-1111-1111-1111-111111111111",
                    DisplayName = displayName ?? organizationUrl,
                    EncryptedSecretMaterial = "protected-secret",
                    VerificationStatus = "verified",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow,
                });

            var record = new ClientScmScopeRecord
            {
                Id = Guid.NewGuid(),
                ClientId = resolvedClientId,
                ConnectionId = connectionId,
                ScopeType = "organization",
                ExternalScopeId = "test-org",
                ScopePath = organizationUrl,
                DisplayName = displayName ?? organizationUrl,
                IsEnabled = isEnabled,
                VerificationStatus = "verified",
                LastVerifiedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            db.ClientScmScopes.Add(record);
            await db.SaveChangesAsync();
            return record.Id;
        }

        public async Task<Guid> GetOnlyTrackedBranchIdAsync(Guid sourceId)
        {
            using var scope = this.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            return await db.ProCursorTrackedBranches
                .Where(branch => branch.KnowledgeSourceId == sourceId)
                .Select(branch => branch.Id)
                .SingleAsync();
        }

        public async Task ResetAsync()
        {
            using var scope = this.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();

            db.ProCursorTokenUsageRollups.RemoveRange(db.ProCursorTokenUsageRollups);
            db.ProCursorTokenUsageEvents.RemoveRange(db.ProCursorTokenUsageEvents);
            db.ProCursorSymbolEdges.RemoveRange(db.ProCursorSymbolEdges);
            db.ProCursorSymbolRecords.RemoveRange(db.ProCursorSymbolRecords);
            db.ProCursorKnowledgeChunks.RemoveRange(db.ProCursorKnowledgeChunks);
            db.ProCursorIndexSnapshots.RemoveRange(db.ProCursorIndexSnapshots);
            db.ProCursorIndexJobs.RemoveRange(db.ProCursorIndexJobs);
            db.ProCursorTrackedBranches.RemoveRange(db.ProCursorTrackedBranches);
            db.ProCursorKnowledgeSources.RemoveRange(db.ProCursorKnowledgeSources);
            db.ClientScmScopes.RemoveRange(db.ClientScmScopes);
            db.ClientScmConnections.RemoveRange(db.ClientScmConnections);
            await db.SaveChangesAsync();
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
            builder.UseSetting("DB_CONNECTION_STRING", string.Empty);
            builder.UseSetting("MEISTER_JWT_SECRET", TestJwtSecret);
            builder.UseSetting("PROCURSOR_REFRESH_POLL_SECONDS", "17");

            var dbName = this._dbName;
            var dbRoot = this._dbRoot;
            var clientId = this.ClientId;
            var otherClientId = this.OtherClientId;
            var clientAdministratorUserId = this.ClientAdministratorUserId;
            var clientUserId = this.ClientUserId;

            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IJwtTokenService, JwtTokenService>();
                services.AddDbContext<MeisterProPRDbContext>(options => options.UseInMemoryDatabase(dbName, dbRoot));
                services.AddDbContextFactory<MeisterProPRDbContext>(options =>
                    options.UseInMemoryDatabase(dbName, dbRoot));
                services.AddScoped<IClientAdminService, ClientAdminService>();
                services.AddScoped<IClientScmConnectionRepository, ClientScmConnectionRepository>();
                services.AddScoped<IClientAdoOrganizationScopeRepository, ClientAdoOrganizationScopeRepository>();
                services.AddScoped<IProCursorKnowledgeSourceRepository, ProCursorKnowledgeSourceRepository>();
                services.AddScoped<IProCursorIndexJobRepository, ProCursorIndexJobRepository>();
                services.AddScoped<IProCursorIndexSnapshotRepository, ProCursorIndexSnapshotRepository>();
                services.AddScoped<IProCursorTokenUsageReadRepository, ProCursorTokenUsageReadRepository>();
                services.AddScoped<ProCursorSymbolGraphRepository>();
                services.AddScoped<IProCursorSymbolGraphRepository>(sp =>
                    sp.GetRequiredService<ProCursorSymbolGraphRepository>());

                ReplaceService(services, Substitute.For<IPullRequestFetcher>());
                ReplaceService(services, Substitute.For<IAdoCommentPoster>());
                ReplaceService(services, Substitute.For<IAssignedReviewDiscoveryService>());

                var userRepo = Substitute.For<IUserRepository>();
                userRepo.GetUserClientRolesAsync(clientAdministratorUserId, Arg.Any<CancellationToken>())
                    .Returns(
                        Task.FromResult(
                            new Dictionary<Guid, ClientRole>
                            {
                                { clientId, ClientRole.ClientAdministrator },
                            }));
                userRepo.GetUserClientRolesAsync(clientUserId, Arg.Any<CancellationToken>())
                    .Returns(
                        Task.FromResult(
                            new Dictionary<Guid, ClientRole>
                            {
                                { clientId, ClientRole.ClientUser },
                            }));
                userRepo.GetUserClientRolesAsync(
                        Arg.Is<Guid>(id => id != clientAdministratorUserId && id != clientUserId),
                        Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new Dictionary<Guid, ClientRole>()));
                userRepo.GetByIdWithAssignmentsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AppUser?>(null));
                services.AddSingleton(userRepo);

                var crawlRepo = Substitute.For<ICrawlConfigurationRepository>();
                crawlRepo.GetAllActiveAsync(Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<CrawlConfigurationDto>>([]));
                services.AddSingleton(crawlRepo);

                services.AddScoped<IProviderAdminDiscoveryService>(sp =>
                    new TestProviderAdminDiscoveryService(
                        this.AdoDiscoveryService,
                        sp.GetRequiredService<MeisterProPRDbContext>()));
                services.AddScoped<IScmProviderRegistry>(sp =>
                {
                    var providerRegistry = Substitute.For<IScmProviderRegistry>();
                    providerRegistry.IsRegistered(ScmProvider.AzureDevOps).Returns(true);
                    providerRegistry.GetProviderAdminDiscoveryService(ScmProvider.AzureDevOps)
                        .Returns(sp.GetRequiredService<IProviderAdminDiscoveryService>());
                    return providerRegistry;
                });

                services.AddSingleton(Substitute.For<IProtocolRecorder>());
                services.AddSingleton(Substitute.For<IMemoryActivityLog>());
                services.AddSingleton(Substitute.For<IThreadMemoryRepository>());
                services.AddSingleton(Substitute.For<IProCursorTokenUsageRebuildService>());

                services.AddSingleton(Substitute.For<IJobRepository>());
                services.AddSingleton(Substitute.For<IClientRegistry>());

                var aiConnectionRepository = Substitute.For<IAiConnectionRepository>();
                aiConnectionRepository.GetByClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<AiConnectionDto>>([]));
                aiConnectionRepository.GetForTierAsync(
                        clientId,
                        AiConnectionModelCategory.Embedding,
                        Arg.Any<CancellationToken>())
                    .Returns(
                        Task.FromResult<AiConnectionDto?>(
                            new AiConnectionDto(
                                Guid.NewGuid(),
                                clientId,
                                "Embedding Connection",
                                "https://embeddings.openai.azure.com/",
                                ["text-embedding-3-small"],
                                false,
                                "text-embedding-3-small",
                                DateTimeOffset.UtcNow,
                                AiConnectionModelCategory.Embedding,
                                [
                                    new AiConnectionModelCapabilityDto(
                                        "text-embedding-3-small",
                                        "cl100k_base",
                                        8192,
                                        1536),
                                ])));
                aiConnectionRepository.GetActiveForClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<AiConnectionDto?>(null));
                services.AddSingleton(aiConnectionRepository);
            });
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = base.CreateHost(builder);

            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            db.Clients.AddRange(
                new ClientRecord
                {
                    Id = this.ClientId,
                    DisplayName = "ProCursor Client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                },
                new ClientRecord
                {
                    Id = this.OtherClientId,
                    DisplayName = "Other Client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            db.SaveChanges();

            return host;
        }

        private string GenerateToken(Guid userId, AppUserRole role)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestJwtSecret));
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var descriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(
                    new[]
                    {
                        new Claim("sub", userId.ToString()),
                        new Claim("global_role", role.ToString()),
                    }),
                Expires = DateTime.UtcNow.AddHours(1),
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
                Issuer = "meisterpropr",
                Audience = "meisterpropr",
            };

            return handler.WriteToken(handler.CreateToken(descriptor));
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

        private sealed class TestProviderAdminDiscoveryService(
            IProviderAdminDiscoveryService adoDiscoveryService,
            MeisterProPRDbContext dbContext) : IProviderAdminDiscoveryService
        {
            public ScmProvider Provider => ScmProvider.AzureDevOps;

            public async Task<ClientScmScopeDto?> GetScopeAsync(
                Guid clientId,
                Guid scopeId,
                CancellationToken ct = default)
            {
                var scope = await dbContext.ClientScmScopes
                    .AsNoTracking()
                    .SingleOrDefaultAsync(record => record.ClientId == clientId && record.Id == scopeId, ct);

                return scope is null
                    ? null
                    : new ClientScmScopeDto(
                        scope.Id,
                        scope.ClientId,
                        scope.ConnectionId,
                        scope.ScopeType,
                        scope.ExternalScopeId,
                        scope.ScopePath,
                        scope.DisplayName ?? string.Empty,
                        scope.VerificationStatus,
                        scope.IsEnabled,
                        scope.LastVerifiedAt,
                        scope.LastVerificationError,
                        scope.CreatedAt,
                        scope.UpdatedAt);
            }

            public Task<IReadOnlyList<AdoProjectOptionDto>> ListProjectsAsync(
                Guid clientId,
                Guid scopeId,
                CancellationToken ct = default)
            {
                return adoDiscoveryService.ListProjectsAsync(clientId, scopeId, ct);
            }

            public Task<IReadOnlyList<AdoSourceOptionDto>> ListSourcesAsync(
                Guid clientId,
                Guid scopeId,
                string projectId,
                ProCursorSourceKind sourceKind,
                CancellationToken ct = default)
            {
                return adoDiscoveryService.ListSourcesAsync(clientId, scopeId, projectId, sourceKind, ct);
            }

            public Task<IReadOnlyList<AdoBranchOptionDto>> ListBranchesAsync(
                Guid clientId,
                Guid scopeId,
                string projectId,
                ProCursorSourceKind sourceKind,
                CanonicalSourceReferenceDto canonicalSourceRef,
                CancellationToken ct = default)
            {
                return adoDiscoveryService.ListBranchesAsync(
                    clientId,
                    scopeId,
                    projectId,
                    sourceKind,
                    canonicalSourceRef,
                    ct);
            }

            public Task<IReadOnlyList<AdoCrawlFilterOptionDto>> ListCrawlFiltersAsync(
                Guid clientId,
                Guid scopeId,
                string projectId,
                CancellationToken ct = default)
            {
                return adoDiscoveryService.ListCrawlFiltersAsync(clientId, scopeId, projectId, ct);
            }
        }
    }
}
