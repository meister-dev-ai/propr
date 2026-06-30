// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Clients.Support;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.Clients.Support;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Services;
using MeisterProPR.Infrastructure.Tests.GitHub;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

public sealed class ClientScmConnectionRepositoryTests : IDisposable
{
    private readonly MeisterProPRDbContext _dbContext;
    private readonly ClientScmConnectionRepository _repository;

    public ClientScmConnectionRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"ClientScmConnectionRepositoryTests-{Guid.NewGuid():N}")
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);
        this._repository = new ClientScmConnectionRepository(this._dbContext, CreateCodec());
    }

    public void Dispose()
    {
        this._dbContext.Dispose();
    }

    [Fact]
    public async Task AddAsync_GitHubAppInstallation_PersistsGitHubAppMetadataAndProtectedSecret()
    {
        var client = await this.SeedClientAsync();
        var privateKeyPem = GitHubAppTestHelpers.CreatePrivateKeyPem(true);

        var created = await this._repository.AddAsync(
            client.Id,
            ScmProvider.GitHub,
            "https://github.enterprise.example.com/acme/platform",
            ScmAuthenticationKind.AppInstallation,
            null,
            null,
            "GitHub App",
            privateKeyPem,
            true,
            123456,
            789012,
            ct: CancellationToken.None);

        Assert.NotNull(created);
        Assert.Equal(123456, created!.GitHubAppId);
        Assert.Equal(789012, created.GitHubAppInstallationId);

        var record = await this._dbContext.ClientScmConnections.SingleAsync(connection => connection.Id == created.Id);
        Assert.Equal(123456, record.GitHubAppId);
        Assert.Equal(789012, record.GitHubAppInstallationId);
        Assert.NotEqual(privateKeyPem, record.EncryptedSecretMaterial);
    }

    [Fact]
    public async Task AddAsync_WithoutRetentionSettings_DefaultsToDisabledAndNullWindow()
    {
        var client = await this.SeedClientAsync();

        var created = await this._repository.AddAsync(
            client.Id,
            ScmProvider.GitHub,
            "https://github.com",
            ScmAuthenticationKind.PersonalAccessToken,
            null,
            null,
            "GitHub PAT",
            "ghp_default_secret",
            true,
            ct: CancellationToken.None);

        Assert.NotNull(created);
        Assert.False(created!.StoreThreads);
        Assert.False(created.StoreDiffs);
        Assert.Null(created.RetentionDays);

        var record = await this._dbContext.ClientScmConnections.SingleAsync(connection => connection.Id == created.Id);
        Assert.False(record.StoreThreads);
        Assert.False(record.StoreDiffs);
        Assert.Null(record.RetentionDays);
    }

    [Fact]
    public async Task AddAsync_WithRetentionSettings_PersistsThemThroughDto()
    {
        var client = await this.SeedClientAsync();

        var created = await this._repository.AddAsync(
            client.Id,
            ScmProvider.GitHub,
            "https://github.com",
            ScmAuthenticationKind.PersonalAccessToken,
            null,
            null,
            "GitHub PAT",
            "ghp_retained_secret",
            true,
            storeThreads: true,
            storeDiffs: true,
            retentionDays: 90,
            ct: CancellationToken.None);

        Assert.NotNull(created);
        Assert.True(created!.StoreThreads);
        Assert.True(created.StoreDiffs);
        Assert.Equal(90, created.RetentionDays);

        var record = await this._dbContext.ClientScmConnections.SingleAsync(connection => connection.Id == created.Id);
        Assert.True(record.StoreThreads);
        Assert.True(record.StoreDiffs);
        Assert.Equal(90, record.RetentionDays);
    }

    [Fact]
    public async Task UpdateAsync_OverwritesRetentionSettingsFromArguments()
    {
        var client = await this.SeedClientAsync();
        var created = await this._repository.AddAsync(
            client.Id,
            ScmProvider.GitHub,
            "https://github.com",
            ScmAuthenticationKind.PersonalAccessToken,
            null,
            null,
            "GitHub PAT",
            "ghp_initial_secret",
            true,
            storeThreads: true,
            storeDiffs: true,
            retentionDays: 90,
            ct: CancellationToken.None);
        Assert.NotNull(created);

        var updated = await this._repository.UpdateAsync(
            client.Id,
            created!.Id,
            "https://github.com",
            ScmAuthenticationKind.PersonalAccessToken,
            null,
            null,
            "GitHub PAT",
            null,
            true,
            storeThreads: false,
            storeDiffs: false,
            retentionDays: null,
            ct: CancellationToken.None);

        Assert.NotNull(updated);
        Assert.False(updated!.StoreThreads);
        Assert.False(updated.StoreDiffs);
        Assert.Null(updated.RetentionDays);

        var record = await this._dbContext.ClientScmConnections.SingleAsync(connection => connection.Id == created.Id);
        Assert.False(record.StoreThreads);
        Assert.False(record.StoreDiffs);
        Assert.Null(record.RetentionDays);
    }

    [Fact]
    public async Task UpdateAsync_GitHubAppRotation_ReprotectsSecretAndResetsVerification()
    {
        var client = await this.SeedClientAsync();
        var originalSecret = GitHubAppTestHelpers.CreatePrivateKeyPem(true);
        var created = await this._repository.AddAsync(
            client.Id,
            ScmProvider.GitHub,
            "https://github.enterprise.example.com/acme/platform",
            ScmAuthenticationKind.AppInstallation,
            null,
            null,
            "GitHub App",
            originalSecret,
            true,
            123456,
            789012,
            ct: CancellationToken.None);
        Assert.NotNull(created);

        await this._repository.UpdateVerificationAsync(
            client.Id,
            created!.Id,
            "verified",
            DateTimeOffset.UtcNow,
            null,
            CancellationToken.None);

        var rotatedSecret = GitHubAppTestHelpers.CreatePrivateKeyPem(true);
        var updated = await this._repository.UpdateAsync(
            client.Id,
            created.Id,
            "https://github.enterprise.example.com/acme/platform",
            ScmAuthenticationKind.AppInstallation,
            null,
            null,
            "GitHub App",
            rotatedSecret,
            true,
            456123,
            654321,
            ct: CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal("unknown", updated!.VerificationStatus);
        Assert.Null(updated.LastVerifiedAt);
        Assert.Equal(456123, updated.GitHubAppId);
        Assert.Equal(654321, updated.GitHubAppInstallationId);

        var record = await this._dbContext.ClientScmConnections.SingleAsync(connection => connection.Id == created.Id);
        Assert.Equal(456123, record.GitHubAppId);
        Assert.Equal(654321, record.GitHubAppInstallationId);
        Assert.Equal("unknown", record.VerificationStatus);
        Assert.Null(record.LastVerifiedAt);
        Assert.NotNull(record.EncryptedSecretMaterial);
    }

    [Fact]
    public async Task UpdateAsync_GitHubAppToPat_ClearsGitHubAppMetadata()
    {
        var client = await this.SeedClientAsync();
        var created = await this._repository.AddAsync(
            client.Id,
            ScmProvider.GitHub,
            "https://github.enterprise.example.com/acme/platform",
            ScmAuthenticationKind.AppInstallation,
            null,
            null,
            "GitHub App",
            GitHubAppTestHelpers.CreatePrivateKeyPem(true),
            true,
            123456,
            789012,
            ct: CancellationToken.None);
        Assert.NotNull(created);

        var updated = await this._repository.UpdateAsync(
            client.Id,
            created!.Id,
            "https://github.enterprise.example.com/acme/platform",
            ScmAuthenticationKind.PersonalAccessToken,
            null,
            null,
            "GitHub PAT",
            "ghp_rotated_secret",
            true,
            ct: CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Null(updated!.GitHubAppId);
        Assert.Null(updated.GitHubAppInstallationId);

        var record = await this._dbContext.ClientScmConnections.SingleAsync(connection => connection.Id == created.Id);
        Assert.Null(record.GitHubAppId);
        Assert.Null(record.GitHubAppInstallationId);
        Assert.Equal(ScmAuthenticationKind.PersonalAccessToken, record.AuthenticationKind);
    }

    [Fact]
    public async Task GetOperationalConnectionAsync_WithDbContextFactory_SupportsParallelReads()
    {
        var dbRoot = new InMemoryDatabaseRoot();
        var dbName = $"ClientScmConnectionRepositoryParallel-{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase(dbName, dbRoot)
            .Options;

        await using var db = new MeisterProPRDbContext(options);
        var factory = new PooledDbContextFactory<MeisterProPRDbContext>(options);
        var providerActivationService = CreateProviderActivationService(options);
        var repository = new ClientScmConnectionRepository(db, CreateCodec(), providerActivationService, factory);

        var client = await SeedClientAsync(db);
        db.ProviderActivations.Add(
            new ProviderActivationRecord
            {
                Provider = ScmProvider.GitHub,
                IsEnabled = true,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();
        var hostBaseUrl = "https://github.enterprise.example.com";
        var host = new ProviderHostRef(ScmProvider.GitHub, hostBaseUrl);
        await repository.AddAsync(
            client.Id,
            ScmProvider.GitHub,
            hostBaseUrl,
            ScmAuthenticationKind.PersonalAccessToken,
            null,
            null,
            "GitHub PAT",
            "ghp_parallel_secret",
            true,
            ct: CancellationToken.None);

        var tasks = Enumerable.Range(0, 4)
            .Select(_ => repository.GetOperationalConnectionAsync(client.Id, host, CancellationToken.None));

        var results = await Task.WhenAll(tasks);

        Assert.All(
            results, credential =>
            {
                Assert.NotNull(credential);
                Assert.Equal(ScmProvider.GitHub, credential!.ProviderFamily);
                Assert.Equal(hostBaseUrl, credential.HostBaseUrl);
            });
    }

    [Fact]
    public async Task AddAsync_AzureDevOpsServerWindowsAccount_PersistsUserNameAndProtectedSecret()
    {
        var client = await this.SeedClientAsync();

        var created = await this._repository.AddAsync(
            client.Id,
            ScmProvider.AzureDevOps,
            "https://ado-server.example.com/tfs/defaultcollection",
            ScmAuthenticationKind.WindowsUserAccount,
            null,
            null,
            "Azure DevOps Server",
            "super-secret-password",
            true,
            userName: @"CONTOSO\\ado-user",
            ct: CancellationToken.None);

        Assert.NotNull(created);
        Assert.Equal(@"CONTOSO\\ado-user", created!.UserName);

        var record = await this._dbContext.ClientScmConnections.SingleAsync(connection => connection.Id == created.Id);
        Assert.Equal("https://ado-server.example.com/tfs/defaultcollection", record.HostBaseUrl);
        Assert.Equal(@"CONTOSO\\ado-user", record.UserName);
        Assert.NotEqual("super-secret-password", record.EncryptedSecretMaterial);
    }

    [Fact]
    public async Task GetOperationalConnectionAsync_AzureDevOpsScopePathMatchesStoredServerBasePath()
    {
        var client = await this.SeedClientAsync();

        var created = await this._repository.AddAsync(
            client.Id,
            ScmProvider.AzureDevOps,
            "https://ado-server.example.com/tfs",
            ScmAuthenticationKind.PersonalAccessToken,
            null,
            null,
            "Azure DevOps Server",
            "server-pat",
            true,
            ct: CancellationToken.None);

        var resolved = await this._repository.GetOperationalConnectionAsync(
            client.Id,
            new ProviderHostRef(ScmProvider.AzureDevOps, "https://ado-server.example.com/tfs/DefaultCollection"),
            CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(created!.Id, resolved!.Id);
        Assert.Equal("https://ado-server.example.com/tfs", resolved.HostBaseUrl);
    }

    [Fact]
    public async Task UpdateAsync_AzureDevOpsHostChange_RepointsExistingScopeAuthorities()
    {
        var client = await this.SeedClientAsync();

        var created = await this._repository.AddAsync(
            client.Id,
            ScmProvider.AzureDevOps,
            "http://127.0.0.1",
            ScmAuthenticationKind.WindowsUserAccount,
            null,
            null,
            "Azure DevOps Server",
            "super-secret-password",
            true,
            userName: @"CONTOSO\\ado-user",
            ct: CancellationToken.None);

        this._dbContext.ClientScmScopes.Add(
            new ClientScmScopeRecord
            {
                Id = Guid.NewGuid(),
                ClientId = client.Id,
                ConnectionId = created!.Id,
                ScopeType = "organization",
                ExternalScopeId = "defaultcollection",
                ScopePath = "http://127.0.0.1/tfs/defaultcollection",
                DisplayName = "Default Collection",
                VerificationStatus = "verified",
                IsEnabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        await this._dbContext.SaveChangesAsync();

        await this._repository.UpdateAsync(
            client.Id,
            created.Id,
            "http://172.24.241.1:8060",
            ScmAuthenticationKind.WindowsUserAccount,
            null,
            null,
            "Azure DevOps Server",
            null,
            true,
            userName: @"CONTOSO\\ado-user",
            ct: CancellationToken.None);

        var scope = await this._dbContext.ClientScmScopes.SingleAsync(candidate => candidate.ConnectionId == created.Id);
        Assert.Equal("http://172.24.241.1:8060/tfs/defaultcollection", scope.ScopePath);
    }

    [Fact]
    public async Task UpdateAsync_AzureDevOpsHostChange_RepointsExistingScopeServerBasePath()
    {
        var client = await this.SeedClientAsync();

        var created = await this._repository.AddAsync(
            client.Id,
            ScmProvider.AzureDevOps,
            "https://ado-server.example.com/tfs",
            ScmAuthenticationKind.WindowsUserAccount,
            null,
            null,
            "Azure DevOps Server",
            "super-secret-password",
            true,
            userName: @"CONTOSO\\ado-user",
            ct: CancellationToken.None);

        this._dbContext.ClientScmScopes.Add(
            new ClientScmScopeRecord
            {
                Id = Guid.NewGuid(),
                ClientId = client.Id,
                ConnectionId = created!.Id,
                ScopeType = "organization",
                ExternalScopeId = "defaultcollection",
                ScopePath = "https://ado-server.example.com/tfs/defaultcollection",
                DisplayName = "Default Collection",
                VerificationStatus = "verified",
                IsEnabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        await this._dbContext.SaveChangesAsync();

        await this._repository.UpdateAsync(
            client.Id,
            created.Id,
            "https://ado-server.example.com/ado",
            ScmAuthenticationKind.WindowsUserAccount,
            null,
            null,
            "Azure DevOps Server",
            null,
            true,
            userName: @"CONTOSO\\ado-user",
            ct: CancellationToken.None);

        var record = await this._dbContext.ClientScmConnections.SingleAsync(connection => connection.Id == created.Id);
        var scope = await this._dbContext.ClientScmScopes.SingleAsync(candidate => candidate.ConnectionId == created.Id);
        Assert.Equal("https://ado-server.example.com/ado", record.HostBaseUrl);
        Assert.Equal("https://ado-server.example.com/ado/defaultcollection", scope.ScopePath);
    }

    [Fact]
    public async Task GetAllForRetentionSweepAsync_ReturnsRetentionSettingsAcrossClients()
    {
        var firstClient = await this.SeedClientAsync();
        var secondClient = await this.SeedClientAsync();

        var enabled = await this._repository.AddAsync(
            firstClient.Id,
            ScmProvider.GitHub,
            "https://github.com",
            ScmAuthenticationKind.PersonalAccessToken,
            null,
            null,
            "Enabled retention",
            "ghp_enabled",
            true,
            storeThreads: true,
            storeDiffs: false,
            retentionDays: 90,
            ct: CancellationToken.None);
        Assert.NotNull(enabled);

        var disabled = await this._repository.AddAsync(
            secondClient.Id,
            ScmProvider.GitHub,
            "https://github.example.com",
            ScmAuthenticationKind.PersonalAccessToken,
            null,
            null,
            "Disabled retention",
            "ghp_disabled",
            true,
            ct: CancellationToken.None);
        Assert.NotNull(disabled);

        var settings = await this._repository.GetAllForRetentionSweepAsync(CancellationToken.None);

        Assert.Equal(2, settings.Count);

        var enabledSettings = Assert.Single(settings, candidate => candidate.Id == enabled!.Id);
        Assert.Equal(firstClient.Id, enabledSettings.ClientId);
        Assert.True(enabledSettings.StoreThreads);
        Assert.False(enabledSettings.StoreDiffs);
        Assert.Equal(90, enabledSettings.RetentionDays);

        var disabledSettings = Assert.Single(settings, candidate => candidate.Id == disabled!.Id);
        Assert.Equal(secondClient.Id, disabledSettings.ClientId);
        Assert.False(disabledSettings.StoreThreads);
        Assert.False(disabledSettings.StoreDiffs);
        Assert.Null(disabledSettings.RetentionDays);
    }

    private async Task<ClientRecord> SeedClientAsync()
    {
        return await SeedClientAsync(this._dbContext);
    }

    private static async Task<ClientRecord> SeedClientAsync(MeisterProPRDbContext dbContext)
    {
        var client = new ClientRecord
        {
            Id = Guid.NewGuid(),
            TenantId = TenantCatalog.SystemTenantId,
            DisplayName = "Repository Test Client",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        dbContext.Clients.Add(client);
        await dbContext.SaveChangesAsync();
        return client;
    }

    private static ProviderActivationService CreateProviderActivationService(DbContextOptions<MeisterProPRDbContext> options)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<MeisterProPRDbContext>>(_ => new PooledDbContextFactory<MeisterProPRDbContext>(options));
        services.AddSingleton<IScmProviderRegistry, TestScmProviderRegistry>();

        var provider = services.BuildServiceProvider();
        return new ProviderActivationService(
            new MeisterProPRDbContext(options),
            provider.GetRequiredService<IDbContextFactory<MeisterProPRDbContext>>(),
            provider,
            new StaticProviderReadinessProfileCatalog());
    }

    private static ISecretProtectionCodec CreateCodec()
    {
        var keysDirectory = Path.Combine(Path.GetTempPath(), $"MeisterProPR.ClientScmConnectionRepositoryTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysDirectory);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("MeisterProPR.Tests")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

        var provider = services.BuildServiceProvider();
        return new SecretProtectionCodec(provider.GetRequiredService<IDataProtectionProvider>());
    }

    private sealed class TestScmProviderRegistry : IScmProviderRegistry
    {
        public bool IsRegistered(ScmProvider provider)
        {
            return true;
        }

        public IReadOnlyList<string> GetRegisteredCapabilities(ScmProvider provider)
        {
            return [];
        }

        public IRepositoryDiscoveryProvider GetRepositoryDiscoveryProvider(ScmProvider provider)
        {
            throw new NotSupportedException();
        }

        public ICodeReviewQueryService GetCodeReviewQueryService(ScmProvider provider)
        {
            throw new NotSupportedException();
        }

        public ICodeReviewPublicationService GetCodeReviewPublicationService(ScmProvider provider)
        {
            throw new NotSupportedException();
        }

        public IReviewDiscoveryProvider GetReviewDiscoveryProvider(ScmProvider provider)
        {
            throw new NotSupportedException();
        }

        public IReviewerIdentityService GetReviewerIdentityService(ScmProvider provider)
        {
            throw new NotSupportedException();
        }

        public IReviewAssignmentService GetReviewAssignmentService(ScmProvider provider)
        {
            throw new NotSupportedException();
        }

        public IReviewThreadStatusWriter GetReviewThreadStatusWriter(ScmProvider provider)
        {
            throw new NotSupportedException();
        }

        public IReviewThreadReplyPublisher GetReviewThreadReplyPublisher(ScmProvider provider)
        {
            throw new NotSupportedException();
        }

        public IProviderAdminDiscoveryService GetProviderAdminDiscoveryService(ScmProvider provider)
        {
            throw new NotSupportedException();
        }

        public IWebhookIngressService GetWebhookIngressService(ScmProvider provider)
        {
            throw new NotSupportedException();
        }
    }
}
