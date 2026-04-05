// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.DTOs.AzureDevOps;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using FactAttribute = Xunit.SkippableFactAttribute;
using TheoryAttribute = Xunit.SkippableTheoryAttribute;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Integration tests for <see cref="CrawlConfigurationRepository" /> against a real PostgreSQL instance.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class CrawlConfigurationRepositoryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private MeisterProPRDbContext _dbContext = null!;
    private CrawlConfigurationRepository _repo = null!;
    private Guid _clientId;
    private Guid _otherClientId;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);

        // Seed a client for FK constraint
        this._clientId = Guid.NewGuid();
        this._dbContext.Clients.Add(new ClientRecord
        {
            Id = this._clientId,
            DisplayName = "Test Client",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        this._otherClientId = Guid.NewGuid();
        this._dbContext.Clients.Add(new ClientRecord
        {
            Id = this._otherClientId,
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

        await this._dbContext.ClientAdoOrganizationScopes
            .Where(scope => clientIds.Contains(scope.ClientId))
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

    private async Task<Guid> SeedOrganizationScopeAsync(string organizationUrl = "https://dev.azure.com/org")
    {
        var scopeId = Guid.NewGuid();
        this._dbContext.ClientAdoOrganizationScopes.Add(new ClientAdoOrganizationScopeRecord
        {
            Id = scopeId,
            ClientId = this._clientId,
            OrganizationUrl = organizationUrl,
            DisplayName = "Org",
            IsEnabled = true,
            VerificationStatus = AdoOrganizationVerificationStatus.Verified,
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
            repositoryId: "repo-b",
            branchFilter: null,
            ct: default);

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
            repositoryId: "repo-a",
            branchFilter: "main",
            ct: default);

        Assert.True(result);
    }

    [Fact]
    public async Task AddAsync_PersistsOrganizationScopeId()
    {
        var organizationScopeId = await this.SeedOrganizationScopeAsync();

        var created = await this._repo.AddAsync(
            this._clientId,
            "https://dev.azure.com/org",
            "project",
            60,
            organizationScopeId,
            CancellationToken.None);

        var stored = await this._dbContext.CrawlConfigurations.SingleAsync(config => config.Id == created.Id);

        Assert.Equal(organizationScopeId, created.OrganizationScopeId);
        Assert.Equal(organizationScopeId, stored.OrganizationScopeId);
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
                    "Repository One")
            ],
            CancellationToken.None);

        var storedFilter = await this._dbContext.CrawlRepoFilters.SingleAsync(filter => filter.CrawlConfigurationId == config.Id);

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
                }
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
                }
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
        Assert.Contains(disabledSourceId, dto.InvalidProCursorSourceIds);
        Assert.Single(dto.RepoFilters);
        Assert.Equal("Repository One", dto.RepoFilters[0].DisplayName);
        Assert.Equal("azureDevOps", dto.RepoFilters[0].CanonicalSourceRef!.Provider);
        Assert.Equal("repo-1", dto.RepoFilters[0].CanonicalSourceRef!.Value);
    }
}
