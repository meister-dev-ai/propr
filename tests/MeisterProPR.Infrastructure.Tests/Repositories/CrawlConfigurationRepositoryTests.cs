// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Integration tests for <see cref="CrawlConfigurationRepository" /> against a real PostgreSQL instance.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class CrawlConfigurationRepositoryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private Guid _clientId;
    private MeisterProPRDbContext _dbContext = null!;
    private Guid _otherClientId;
    private CrawlConfigurationRepository _repo = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);

        // Seed a client for FK constraint
        this._clientId = Guid.NewGuid();
        this._dbContext.Clients.Add(
            new ClientRecord
            {
                Id = this._clientId,
                TenantId = TenantCatalog.SystemTenantId,
                DisplayName = "Test Client",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        this._otherClientId = Guid.NewGuid();
        this._dbContext.Clients.Add(
            new ClientRecord
            {
                Id = this._otherClientId,
                TenantId = TenantCatalog.SystemTenantId,
                DisplayName = "Other Test Client",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        await this._dbContext.SaveChangesAsync();

        this._repo = new CrawlConfigurationRepository(this._dbContext);
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is null)
        {
            return;
        }

        var clientIds = new[] { this._clientId, this._otherClientId };
        var configIds = await this._dbContext.CrawlConfigurations
            .Where(c => clientIds.Contains(c.ClientId))
            .Select(c => c.Id)
            .ToListAsync();

        if (configIds.Count > 0)
        {
            await this._dbContext.CrawlConfigurationProCursorSources
                .Where(link => configIds.Contains(link.CrawlConfigurationId))
                .ExecuteDeleteAsync();
            await this._dbContext.CrawlRepoFilters
                .Where(filter => configIds.Contains(filter.CrawlConfigurationId))
                .ExecuteDeleteAsync();
            await this._dbContext.CrawlConfigurations
                .Where(c => configIds.Contains(c.Id))
                .ExecuteDeleteAsync();
        }

        var sourceIds = await this._dbContext.ProCursorKnowledgeSources
            .Where(source => clientIds.Contains(source.ClientId))
            .Select(source => source.Id)
            .ToListAsync();

        if (sourceIds.Count > 0)
        {
            await this._dbContext.ProCursorTrackedBranches
                .Where(branch => sourceIds.Contains(branch.KnowledgeSourceId))
                .ExecuteDeleteAsync();
            await this._dbContext.ProCursorKnowledgeSources
                .Where(source => sourceIds.Contains(source.Id))
                .ExecuteDeleteAsync();
        }

        await this._dbContext.ClientScmScopes
            .Where(scope => clientIds.Contains(scope.ClientId))
            .ExecuteDeleteAsync();
        await this._dbContext.ClientScmConnections
            .Where(connection => clientIds.Contains(connection.ClientId))
            .ExecuteDeleteAsync();
        await this._dbContext.Clients
            .Where(c => clientIds.Contains(c.Id))
            .ExecuteDeleteAsync();
        await this._dbContext.DisposeAsync();
    }

    private async Task<CrawlConfigurationRecord> SeedConfig(
        string orgUrl = "https://dev.azure.com/org",
        string projectId = "project",
        string? repositoryId = null,
        string? branchFilter = null)
    {
        var record = new CrawlConfigurationRecord
        {
            Id = Guid.NewGuid(),
            ClientId = this._clientId,
            OrganizationUrl = orgUrl,
            ProjectId = projectId,
            RepositoryId = repositoryId,
            BranchFilter = branchFilter,
            CrawlIntervalSeconds = 60,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        this._dbContext.CrawlConfigurations.Add(record);
        await this._dbContext.SaveChangesAsync();
        return record;
    }

    private async Task<Guid> SeedAzureConnectionAsync()
    {
        var connectionId = Guid.NewGuid();
        this._dbContext.ClientScmConnections.Add(
            new ClientScmConnectionRecord
            {
                Id = connectionId,
                ClientId = this._clientId,
                Provider = ScmProvider.AzureDevOps,
                HostBaseUrl = "https://dev.azure.com",
                AuthenticationKind = ScmAuthenticationKind.OAuthClientCredentials,
                OAuthTenantId = "contoso.onmicrosoft.com",
                OAuthClientId = "11111111-1111-1111-1111-111111111111",
                DisplayName = "Azure DevOps",
                EncryptedSecretMaterial = "protected-secret",
                VerificationStatus = "verified",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        await this._dbContext.SaveChangesAsync();
        return connectionId;
    }

    private async Task<Guid> SeedOrganizationScopeAsync(string organizationUrl = "https://dev.azure.com/org")
    {
        var connectionId = await this.SeedAzureConnectionAsync();
        var scopeId = Guid.NewGuid();
        this._dbContext.ClientScmScopes.Add(
            new ClientScmScopeRecord
            {
                Id = scopeId,
                ClientId = this._clientId,
                ConnectionId = connectionId,
                ScopeType = "organization",
                ExternalScopeId = "org",
                ScopePath = organizationUrl,
                DisplayName = "Org",
                IsEnabled = true,
                VerificationStatus = "verified",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        await this._dbContext.SaveChangesAsync();
        return scopeId;
    }

    private async Task<Guid> SeedKnowledgeSourceAsync(Guid clientId, string displayName, bool isEnabled)
    {
        var source = new ProCursorKnowledgeSource(
            Guid.NewGuid(),
            clientId,
            displayName,
            ProCursorSourceKind.Repository,
            "https://dev.azure.com/org",
            "project",
            $"repo-{Guid.NewGuid():N}",
            "main",
            null,
            isEnabled,
            "auto");

        this._dbContext.ProCursorKnowledgeSources.Add(source);
        await this._dbContext.SaveChangesAsync();
        return source.Id;
    }

    // T039: same ClientId + OrgUrl + ProjectId but different RepositoryId → NOT a duplicate
    [Fact]
    public async Task ExistsAsync_ReturnsFalse_WhenSameOrgProjectButDifferentRepo()
    {
        await this.SeedConfig(repositoryId: "repo-a");

        var result = await this._repo.ExistsAsync(
            this._clientId,
            "https://dev.azure.com/org",
            "project",
            "repo-b",
            null);

        Assert.False(result);
    }

    // T040: exact duplicate of all five fields → IS a duplicate
    [Fact]
    public async Task ExistsAsync_ReturnsTrue_WhenAllFiveFieldsMatch()
    {
        await this.SeedConfig(repositoryId: "repo-a", branchFilter: "main");

        var result = await this._repo.ExistsAsync(
            this._clientId,
            "https://dev.azure.com/org",
            "project",
            "repo-a",
            "main");

        Assert.True(result);
    }

    [Fact]
    public async Task AddAsync_PersistsOrganizationScopeId()
    {
        var organizationScopeId = await this.SeedOrganizationScopeAsync();

        var created = await this._repo.AddAsync(
            this._clientId,
            ScmProvider.AzureDevOps,
            "https://dev.azure.com/org",
            "project",
            60,
            organizationScopeId,
            CancellationToken.None,
            reviewTemperature: 0.2f);

        var stored = await this._dbContext.CrawlConfigurations.SingleAsync(config => config.Id == created.Id);

        Assert.Equal(organizationScopeId, created.OrganizationScopeId);
        Assert.Equal(organizationScopeId, stored.OrganizationScopeId);
        Assert.Equal(0.2f, created.ReviewTemperature);
        Assert.Equal(0.2f, stored.ReviewTemperature);
    }

    [Fact]
    public async Task UpdateAsync_PersistsReviewTemperature()
    {
        var created = await this._repo.AddAsync(
            this._clientId,
            ScmProvider.AzureDevOps,
            "https://dev.azure.com/org",
            "project",
            60,
            null,
            CancellationToken.None);

        var updated = await this._repo.UpdateAsync(
            created.Id,
            null,
            null,
            this._clientId,
            CancellationToken.None,
            reviewTemperature: 0.45f,
            shouldUpdateReviewTemperature: true);

        var stored = await this._dbContext.CrawlConfigurations.SingleAsync(config => config.Id == created.Id);
        var fetched = await this._repo.GetByIdAsync(created.Id, CancellationToken.None);

        Assert.True(updated);
        Assert.Equal(0.45f, stored.ReviewTemperature);
        Assert.NotNull(fetched);
        Assert.Equal(0.45f, fetched.ReviewTemperature);
    }

    [Fact]
    public async Task UpdateAsync_ClearsReviewTemperature_WhenExplicitlySpecifiedAsNull()
    {
        var created = await this._repo.AddAsync(
            this._clientId,
            ScmProvider.AzureDevOps,
            "https://dev.azure.com/org",
            "project",
            60,
            null,
            CancellationToken.None,
            reviewTemperature: 0.45f);

        var updated = await this._repo.UpdateAsync(
            created.Id,
            null,
            null,
            this._clientId,
            CancellationToken.None,
            reviewTemperature: null,
            shouldUpdateReviewTemperature: true);

        var stored = await this._dbContext.CrawlConfigurations.SingleAsync(config => config.Id == created.Id);
        var fetched = await this._repo.GetByIdAsync(created.Id, CancellationToken.None);

        Assert.True(updated);
        Assert.Null(stored.ReviewTemperature);
        Assert.NotNull(fetched);
        Assert.Null(fetched.ReviewTemperature);
    }

    [Fact]
    public async Task UpdateRepoFiltersAsync_PersistsCanonicalRepoFilterMetadata()
    {
        var config = await this.SeedConfig();

        var updated = await this._repo.UpdateRepoFiltersAsync(
            config.Id,
            [
                new CrawlRepoFilterDto(
                    Guid.Empty,
                    "Repository One",
                    ["main"],
                    new CanonicalSourceReferenceDto("azureDevOps", "repo-1"),
                    "Repository One"),
            ],
            CancellationToken.None);

        var storedFilter =
            await this._dbContext.CrawlRepoFilters.SingleAsync(filter => filter.CrawlConfigurationId == config.Id);

        Assert.True(updated);
        Assert.Equal("azureDevOps", storedFilter.SourceProvider);
        Assert.Equal("repo-1", storedFilter.CanonicalSourceRef);
        Assert.Equal("Repository One", storedFilter.DisplayName);
        Assert.Equal(["main"], storedFilter.TargetBranchPatterns);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsOrganizationScopeCanonicalFiltersAndInvalidSelectedSources()
    {
        var organizationScopeId = await this.SeedOrganizationScopeAsync();
        var validSourceId = await this.SeedKnowledgeSourceAsync(this._clientId, "Valid Source", true);
        var disabledSourceId = await this.SeedKnowledgeSourceAsync(this._clientId, "Disabled Source", false);

        var config = new CrawlConfigurationRecord
        {
            Id = Guid.NewGuid(),
            ClientId = this._clientId,
            OrganizationUrl = "https://dev.azure.com/org",
            ProjectId = "project",
            OrganizationScopeId = organizationScopeId,
            ProCursorSourceScopeMode = ProCursorSourceScopeMode.SelectedSources,
            CrawlIntervalSeconds = 60,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            RepoFilters =
            [
                new CrawlRepoFilterRecord
                {
                    Id = Guid.NewGuid(),
                    SourceProvider = "azureDevOps",
                    CanonicalSourceRef = "repo-1",
                    DisplayName = "Repository One",
                    RepositoryName = "Repository One",
                    TargetBranchPatterns = ["main"],
                },
            ],
            ProCursorSources =
            [
                new CrawlConfigurationProCursorSourceRecord
                {
                    CrawlConfigurationId = Guid.Empty,
                    ProCursorSourceId = validSourceId,
                    CreatedAt = DateTimeOffset.UtcNow,
                },
                new CrawlConfigurationProCursorSourceRecord
                {
                    CrawlConfigurationId = Guid.Empty,
                    ProCursorSourceId = disabledSourceId,
                    CreatedAt = DateTimeOffset.UtcNow,
                },
            ],
        };
        this._dbContext.CrawlConfigurations.Add(config);
        await this._dbContext.SaveChangesAsync();

        var dto = await this._repo.GetByIdAsync(config.Id, CancellationToken.None);

        Assert.NotNull(dto);
        Assert.Equal(organizationScopeId, dto.OrganizationScopeId);
        Assert.Equal(ProCursorSourceScopeMode.SelectedSources, dto.ProCursorSourceScopeMode);
        Assert.Equal(2, dto.ProCursorSourceIds!.Count);
        Assert.Contains(validSourceId, dto.ProCursorSourceIds);
        Assert.Contains(disabledSourceId, dto.ProCursorSourceIds);
        Assert.Single(dto.InvalidProCursorSourceIds!);
        Assert.Contains(disabledSourceId, dto.InvalidProCursorSourceIds!);
        Assert.Single(dto.RepoFilters);
        Assert.Equal("Repository One", dto.RepoFilters[0].DisplayName);
        Assert.Equal("azureDevOps", dto.RepoFilters[0].CanonicalSourceRef!.Provider);
        Assert.Equal("repo-1", dto.RepoFilters[0].CanonicalSourceRef!.Value);
    }
}
