// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Azure.Core;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.Providers.AzureDevOps.DependencyInjection;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.TeamFoundation.Wiki.WebApi;
using Microsoft.VisualStudio.Services.WebApi;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.AzureDevOps;

/// <summary>
///     Unit tests for <see cref="AdoDiscoveryService" /> using a testable subclass
///     that replaces Azure DevOps SDK calls with in-memory results.
/// </summary>
public sealed class AdoDiscoveryServiceTests
{
    private const string SecretPurpose = "ClientScmConnectionSecret";
    private static readonly Guid ClientId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid ScopeId = Guid.Parse("10000000-0000-0000-0000-000000000002");

    [Fact]
    public async Task ListProjectsAsync_ReturnsSortedProjectOptionsForEnabledScope()
    {
        var service = new TestableAdoDiscoveryService();
        service.SetProjects(
            new TeamProjectReference
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000010"),
                Name = "Zeta",
            },
            new TeamProjectReference
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000011"),
                Name = "Alpha",
            });

        var result = await service.ListProjectsAsync(ClientId, ScopeId, CancellationToken.None);

        Assert.Collection(
            result,
            first =>
            {
                Assert.Equal(ScopeId, first.OrganizationScopeId);
                Assert.Equal("10000000-0000-0000-0000-000000000011", first.ProjectId);
                Assert.Equal("Alpha", first.ProjectName);
            },
            second =>
            {
                Assert.Equal(ScopeId, second.OrganizationScopeId);
                Assert.Equal("10000000-0000-0000-0000-000000000010", second.ProjectId);
                Assert.Equal("Zeta", second.ProjectName);
            });
    }

    [Fact]
    public async Task ListSourcesAsync_RepositoryKind_ReturnsSortedCanonicalRepositoryOptions()
    {
        var service = new TestableAdoDiscoveryService();
        service.SetRepositories(
            "project-1",
            new GitRepository
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000021"),
                Name = "Zeta Repo",
                DefaultBranch = "refs/heads/main",
            },
            new GitRepository
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000022"),
                Name = "Alpha Repo",
                DefaultBranch = "refs/heads/develop",
            });

        var result = await service.ListSourcesAsync(
            ClientId,
            ScopeId,
            "project-1",
            ProCursorSourceKind.Repository,
            CancellationToken.None);

        Assert.Collection(
            result,
            first =>
            {
                Assert.Equal("Repository", first.SourceKind);
                Assert.Equal("azureDevOps", first.CanonicalSourceRef.Provider);
                Assert.Equal("10000000-0000-0000-0000-000000000022", first.CanonicalSourceRef.Value);
                Assert.Equal("Alpha Repo", first.DisplayName);
                Assert.Equal("develop", first.DefaultBranch);
            },
            second =>
            {
                Assert.Equal("Repository", second.SourceKind);
                Assert.Equal("10000000-0000-0000-0000-000000000021", second.CanonicalSourceRef.Value);
                Assert.Equal("Zeta Repo", second.DisplayName);
                Assert.Equal("main", second.DefaultBranch);
            });
    }

    [Fact]
    public async Task ListSourcesAsync_WikiKind_ReturnsSortedCanonicalWikiOptions()
    {
        var service = new TestableAdoDiscoveryService();
        service.SetWikis(
            "project-1",
            new WikiV2
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000031"),
                Name = "Zeta Wiki",
                RepositoryId = Guid.Parse("10000000-0000-0000-0000-000000000041"),
            },
            new WikiV2
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000032"),
                Name = "Alpha Wiki",
                RepositoryId = Guid.Parse("10000000-0000-0000-0000-000000000042"),
            });

        var result = await service.ListSourcesAsync(
            ClientId,
            ScopeId,
            "project-1",
            ProCursorSourceKind.AdoWiki,
            CancellationToken.None);

        Assert.Collection(
            result,
            first =>
            {
                Assert.Equal("AdoWiki", first.SourceKind);
                Assert.Equal("azureDevOps", first.CanonicalSourceRef.Provider);
                Assert.Equal("10000000-0000-0000-0000-000000000032", first.CanonicalSourceRef.Value);
                Assert.Equal("Alpha Wiki", first.DisplayName);
                Assert.Null(first.DefaultBranch);
            },
            second =>
            {
                Assert.Equal("AdoWiki", second.SourceKind);
                Assert.Equal("10000000-0000-0000-0000-000000000031", second.CanonicalSourceRef.Value);
                Assert.Equal("Zeta Wiki", second.DisplayName);
                Assert.Null(second.DefaultBranch);
            });
    }

    [Fact]
    public async Task ListProjectsAsync_DisabledScope_ThrowsInvalidOperationException()
    {
        var service = new TestableAdoDiscoveryService();
        service.SetScopeAvailability(isEnabled: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ListProjectsAsync(ClientId, ScopeId, CancellationToken.None));

        Assert.Contains("disabled", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListBranchesAsync_WikiKind_UsesBackingRepositoryToResolveBranchOptions()
    {
        var wikiId = Guid.Parse("10000000-0000-0000-0000-000000000051");
        var repositoryId = Guid.Parse("10000000-0000-0000-0000-000000000061");
        var service = new TestableAdoDiscoveryService();

        service.SetWikis(
            "project-1",
            new WikiV2
            {
                Id = wikiId,
                Name = "Engineering Wiki",
                RepositoryId = repositoryId,
            });

        service.SetRepository(
            "project-1",
            repositoryId.ToString(),
            new GitRepository
            {
                Id = repositoryId,
                Name = "Engineering Wiki Repo",
                DefaultBranch = "refs/heads/wiki-main",
            });

        service.SetBranches(
            "project-1",
            repositoryId.ToString(),
            new GitBranchStats { Name = "refs/heads/Guides" },
            new GitBranchStats { Name = "refs/heads/wiki-main" },
            new GitBranchStats { Name = "refs/heads/Guides" });

        var result = await service.ListBranchesAsync(
            ClientId,
            ScopeId,
            "project-1",
            ProCursorSourceKind.AdoWiki,
            new CanonicalSourceReferenceDto("azureDevOps", wikiId.ToString()),
            CancellationToken.None);

        Assert.Collection(
            result,
            first =>
            {
                Assert.Equal("Guides", first.BranchName);
                Assert.False(first.IsDefault);
            },
            second =>
            {
                Assert.Equal("wiki-main", second.BranchName);
                Assert.True(second.IsDefault);
            });
    }

    [Fact]
    public async Task ListBranchesAsync_WikiKindWithoutBackingRepository_ReturnsEmptyList()
    {
        var wikiId = Guid.Parse("10000000-0000-0000-0000-000000000081");
        var service = new TestableAdoDiscoveryService();

        service.SetWikis(
            "project-1",
            new WikiV2
            {
                Id = wikiId,
                Name = "Broken Wiki",
                RepositoryId = Guid.Empty,
            });

        var result = await service.ListBranchesAsync(
            ClientId,
            ScopeId,
            "project-1",
            ProCursorSourceKind.AdoWiki,
            new CanonicalSourceReferenceDto("azureDevOps", wikiId.ToString()),
            CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task ListProjectsAsync_UsesProviderBackedConnectionAndScopeResolution()
    {
        await using var db = CreateContext();
        var codec = CreateCodec();
        var clientId = await SeedClientAsync(db);
        var connectionId = await SeedAzureConnectionAsync(db, clientId, codec, "contoso", "secret-abc");
        var scopeId = await SeedAzureOrganizationScopeAsync(db, clientId, connectionId, "https://dev.azure.com/org");
        var service = new TestableAdoDiscoveryService(
            new ClientScmConnectionRepository(db, codec),
            new ClientScmScopeRepository(db));

        service.SetProjects(
            new TeamProjectReference
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000091"),
                Name = "Provider-backed Project",
            });

        var result = await service.ListProjectsAsync(clientId, scopeId, CancellationToken.None);

        var project = Assert.Single(result);
        Assert.Equal(scopeId, project.OrganizationScopeId);
        Assert.Equal(scopeId, service.LastResolvedScope!.Id);
        Assert.Equal("https://dev.azure.com/org", service.LastResolvedScope.OrganizationUrl);
        Assert.NotNull(service.LastResolvedCredentials);
        Assert.Equal("contoso-tenant", service.LastResolvedCredentials!.TenantId);
        Assert.Equal("contoso-client", service.LastResolvedCredentials.ClientId);
        Assert.Equal("secret-abc", service.LastResolvedCredentials.Secret);
    }

    [Fact]
    public void CrawlingServices_RegisterAzureDevOpsDiscoveryUnderProviderNeutralInterface()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["DB_CONNECTION_STRING"] = "Host=localhost;Database=meister;Username=test;Password=test",
                })
            .Build();

        services.AddSingleton(new VssConnectionFactory(Substitute.For<TokenCredential>()));
        services.AddSingleton(Substitute.For<IClientScmConnectionRepository>());
        services.AddSingleton(Substitute.For<IClientScmScopeRepository>());

        services.AddAzureDevOpsCrawlingServices(configuration);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var discoveryService = scope.ServiceProvider
            .GetServices<IProviderAdminDiscoveryService>()
            .Single(service => service.Provider == ScmProvider.AzureDevOps);

        Assert.IsType<AdoDiscoveryService>(discoveryService);
        Assert.DoesNotContain(
            services,
            descriptor => string.Equals(descriptor.ServiceType.Name, "IAdoDiscoveryService", StringComparison.Ordinal));
    }

    private static ISecretProtectionCodec CreateCodec()
    {
        var keysDirectory = Path.Combine(
            Path.GetTempPath(),
            $"MeisterProPR.AdoDiscoveryServiceTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysDirectory);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("MeisterProPR.Tests")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

        var provider = services.BuildServiceProvider();
        return new SecretProtectionCodec(provider.GetRequiredService<IDataProtectionProvider>());
    }

    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"TestDb_AdoDiscovery_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new MeisterProPRDbContext(options);
    }

    private static async Task<Guid> SeedClientAsync(MeisterProPRDbContext db)
    {
        var id = Guid.NewGuid();
        db.Clients.Add(
            new ClientRecord
            {
                Id = id,
                DisplayName = "Test Client",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();
        return id;
    }

    private static async Task<Guid> SeedAzureConnectionAsync(
        MeisterProPRDbContext db,
        Guid clientId,
        ISecretProtectionCodec codec,
        string displayName,
        string secret)
    {
        var connectionId = Guid.NewGuid();
        db.ClientScmConnections.Add(
            new ClientScmConnectionRecord
            {
                Id = connectionId,
                ClientId = clientId,
                Provider = ScmProvider.AzureDevOps,
                HostBaseUrl = "https://dev.azure.com",
                AuthenticationKind = ScmAuthenticationKind.OAuthClientCredentials,
                OAuthTenantId = $"{displayName}-tenant",
                OAuthClientId = $"{displayName}-client",
                DisplayName = displayName,
                EncryptedSecretMaterial = codec.Protect(secret, SecretPurpose),
                VerificationStatus = "verified",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();
        return connectionId;
    }

    private static async Task<Guid> SeedAzureOrganizationScopeAsync(
        MeisterProPRDbContext db,
        Guid clientId,
        Guid connectionId,
        string organizationUrl)
    {
        var scopeId = Guid.NewGuid();
        db.ClientScmScopes.Add(
            new ClientScmScopeRecord
            {
                Id = scopeId,
                ClientId = clientId,
                ConnectionId = connectionId,
                ScopeType = "organization",
                ExternalScopeId = "org",
                ScopePath = organizationUrl,
                DisplayName = "Org",
                VerificationStatus = "verified",
                IsEnabled = true,
                LastVerifiedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();
        return scopeId;
    }

    private sealed class TestableAdoDiscoveryService : AdoDiscoveryService
    {
        private readonly Dictionary<(string ProjectId, string RepositoryId), IReadOnlyList<GitBranchStats>>
            _branchesByRepository = [];

        private readonly IClientScmConnectionRepository _connectionRepository;

        private readonly Dictionary<string, IReadOnlyList<GitRepository>> _repositoriesByProject =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<(string ProjectId, string RepositoryId), GitRepository> _repositoryById = [];
        private readonly IClientScmScopeRepository _scopeRepository;
        private readonly bool _useRepositoryResolution;

        private readonly Dictionary<string, IReadOnlyList<WikiV2>> _wikisByProject =
            new(StringComparer.OrdinalIgnoreCase);

        private IReadOnlyList<TeamProjectReference> _projects = [];
        private bool _scopeEnabled = true;
        private bool _scopeExists = true;

        public TestableAdoDiscoveryService()
            : this(
                Substitute.For<IClientScmConnectionRepository>(),
                Substitute.For<IClientScmScopeRepository>(),
                false)
        {
        }

        public TestableAdoDiscoveryService(
            IClientScmConnectionRepository connectionRepository,
            IClientScmScopeRepository scopeRepository,
            bool useRepositoryResolution = true)
            : base(
                new VssConnectionFactory(Substitute.For<TokenCredential>()),
                connectionRepository,
                scopeRepository)
        {
            this._connectionRepository = connectionRepository;
            this._scopeRepository = scopeRepository;
            this._useRepositoryResolution = useRepositoryResolution;
        }

        public ClientAdoOrganizationScopeDto? LastResolvedScope { get; private set; }

        public AdoServicePrincipalCredentials? LastResolvedCredentials { get; private set; }

        public void SetProjects(params TeamProjectReference[] projects)
        {
            this._projects = projects;
        }

        public void SetRepositories(string projectId, params GitRepository[] repositories)
        {
            this._repositoriesByProject[projectId] = repositories;

            foreach (var repository in repositories)
            {
                this._repositoryById[(projectId, repository.Id.ToString())] = repository;
            }
        }

        public void SetRepository(string projectId, string repositoryId, GitRepository repository)
        {
            this._repositoryById[(projectId, repositoryId)] = repository;
        }

        public void SetBranches(string projectId, string repositoryId, params GitBranchStats[] branches)
        {
            this._branchesByRepository[(projectId, repositoryId)] = branches;
        }

        public void SetWikis(string projectId, params WikiV2[] wikis)
        {
            this._wikisByProject[projectId] = wikis;
        }

        public void SetScopeAvailability(bool exists = true, bool isEnabled = true)
        {
            this._scopeExists = exists;
            this._scopeEnabled = isEnabled;
        }

        protected internal override async
            Task<(ClientAdoOrganizationScopeDto Scope, AdoServicePrincipalCredentials? Credentials, VssConnection
                Connection)> ResolveScopeAsync(
                Guid clientId,
                Guid organizationScopeId,
                CancellationToken ct)
        {
            if (this._useRepositoryResolution)
            {
                var resolvedScope = await AdoProviderAdapterHelpers.ResolveOrganizationScopeByIdAsync(
                                        this._connectionRepository,
                                        this._scopeRepository,
                                        clientId,
                                        organizationScopeId,
                                        ct)
                                    ?? throw new KeyNotFoundException($"Organization scope {organizationScopeId} was not found for client {clientId}.");

                if (!resolvedScope.IsEnabled)
                {
                    throw new InvalidOperationException("The selected organization scope is disabled.");
                }

                var resolvedCredentials = await AdoProviderAdapterHelpers.ResolveCredentialsAsync(
                    this._connectionRepository,
                    clientId,
                    resolvedScope.ScopePath,
                    ct);
                this.LastResolvedScope = AdoProviderAdapterHelpers.ToAdoOrganizationScopeDto(resolvedScope);
                this.LastResolvedCredentials = resolvedCredentials;
                return (this.LastResolvedScope, resolvedCredentials, null!);
            }

            if (!this._scopeExists)
            {
                throw new KeyNotFoundException($"Organization scope {organizationScopeId} was not found for client {clientId}.");
            }

            if (!this._scopeEnabled)
            {
                throw new InvalidOperationException("The selected organization scope is disabled.");
            }

            var scope = new ClientAdoOrganizationScopeDto(
                organizationScopeId,
                clientId,
                "https://dev.azure.com/test-org",
                "Test Org",
                this._scopeEnabled,
                AdoOrganizationVerificationStatus.Verified,
                null,
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);

            this.LastResolvedScope = scope;
            this.LastResolvedCredentials = null;
            return (scope, null, null!);
        }

        protected internal override Task<IReadOnlyList<TeamProjectReference>> GetProjectsAsync(
            VssConnection connection,
            CancellationToken ct)
        {
            return Task.FromResult(this._projects);
        }

        protected internal override Task<IReadOnlyList<GitRepository>> GetRepositoriesAsync(
            VssConnection connection,
            string projectId,
            CancellationToken ct)
        {
            var repositories = this._repositoriesByProject.TryGetValue(projectId, out var value)
                ? value
                : Array.Empty<GitRepository>();

            return Task.FromResult(repositories);
        }

        protected internal override Task<GitRepository> GetRepositoryAsync(
            VssConnection connection,
            string projectId,
            string repositoryId,
            CancellationToken ct)
        {
            if (this._repositoryById.TryGetValue((projectId, repositoryId), out var repository))
            {
                return Task.FromResult(repository);
            }

            throw new KeyNotFoundException($"Repository {repositoryId} was not configured for project {projectId}.");
        }

        protected internal override Task<IReadOnlyList<GitBranchStats>> GetBranchesAsync(
            VssConnection connection,
            string projectId,
            string repositoryId,
            CancellationToken ct)
        {
            var branches = this._branchesByRepository.TryGetValue((projectId, repositoryId), out var value)
                ? value
                : Array.Empty<GitBranchStats>();

            return Task.FromResult(branches);
        }

        protected internal override Task<IReadOnlyList<WikiV2>> GetWikisAsync(
            VssConnection connection,
            string projectId,
            CancellationToken ct)
        {
            var wikis = this._wikisByProject.TryGetValue(projectId, out var value)
                ? value
                : Array.Empty<WikiV2>();

            return Task.FromResult(wikis);
        }
    }
}
