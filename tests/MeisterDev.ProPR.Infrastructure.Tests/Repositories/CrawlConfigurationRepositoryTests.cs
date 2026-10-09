// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Configuration;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using FactAttribute = Xunit.SkippableFactAttribute;
using MeisterDev.ProPR.TestSupport;
using MeisterDev.ProPR.ProCursor.Contracts.Sources;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Integration tests for <see cref="CrawlConfigurationRepository" /> against a real PostgreSQL instance.
/// </summary>
[Collection("PostgresIntegration2")]
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

    [Fact]
    public async Task AddReviewTargetAsync_PersistsInactiveConfigurationAndCanonicalRepositoryFilter()
    {
        var created = await this._repo.AddReviewTargetAsync(this._clientId, ScmProvider.GitHub, "https://github.example.test", "owner", "12345", "repo");

        Assert.False(created.IsActive);
        var stored = await this._dbContext.CrawlConfigurations.AsNoTracking()
            .Include(config => config.RepoFilters)
            .SingleAsync(config => config.Id == created.Id);
        Assert.False(stored.IsActive);
        Assert.Equal("12345", stored.RepositoryId);
        var filter = Assert.Single(stored.RepoFilters);
        Assert.Equal("12345", filter.CanonicalSourceRef);
        Assert.Equal("GitHub", filter.SourceProvider);
        Assert.Equal("repo", filter.RepositoryName);
    }

    [Fact]
    public async Task DestinationPolicy_CreateAndConditionalEditPersistAtomicallyWithoutChangingSettings()
    {
        var created = await this._repo.AddReviewTargetAsync(
            this._clientId, ScmProvider.GitHub, "https://github.example.test", "owner", "12345", "repo",
            targetBranchPatterns: ["refs/heads/main"]);
        Assert.Equal(["main"], Assert.Single(created.RepoFilters).TargetBranchPatterns);
        await this._repo.UpdateAsync(created.Id, 300, true, this._clientId, reviewTemperature: 0.25f, shouldUpdateReviewTemperature: true);
        this._dbContext.ChangeTracker.Clear();

        Assert.False(await this._repo.UpdateReviewTargetPolicyAsync(created, this._clientId, ["main"], ["release/*"]));
        var current = (await this._repo.GetReviewTargetPolicySnapshotAsync(created.Id, this._clientId))!;
        Assert.True(await this._repo.UpdateReviewTargetPolicyAsync(current, this._clientId, ["main"], ["release/*"]));
        Assert.False(await this._repo.UpdateReviewTargetPolicyAsync(created, this._clientId, ["main"], []));
        Assert.False(await this._repo.UpdateReviewTargetPolicyAsync(created, this._otherClientId, ["release/*"], []));

        var stored = (await this._repo.GetByIdAsync(created.Id))!;
        Assert.Equal(["release/*"], Assert.Single(stored.RepoFilters).TargetBranchPatterns);
        Assert.Equal(created.RepoFilters[0].Id, stored.RepoFilters[0].Id);
        Assert.Equal(created.RepoFilters[0].CanonicalSourceRef, stored.RepoFilters[0].CanonicalSourceRef);
        Assert.True(stored.IsActive);
        Assert.Equal(300, stored.CrawlIntervalSeconds);
        Assert.Equal(0.25f, stored.ReviewTemperature);
    }

    [Theory]
    [InlineData("filterReplacement")]
    [InlineData("identity")]
    [InlineData("name")]
    [InlineData("scope")]
    [InlineData("project")]
    [InlineData("provider")]
    public async Task DestinationPolicy_StaleAuthorizedIdentityCannotMutateReplacement(string change)
    {
        var created = await this._repo.AddReviewTargetAsync(
            this._clientId, ScmProvider.GitHub, "https://github.example.test", "owner", "12345", "repo", targetBranchPatterns: ["main"]);
        var record = await this._dbContext.CrawlConfigurations.Include(config => config.RepoFilters).SingleAsync(config => config.Id == created.Id);
        var filter = Assert.Single(record.RepoFilters);
        switch (change)
        {
            case "filterReplacement":
                this._dbContext.CrawlRepoFilters.Remove(filter);
                record.RepoFilters =
                [
                    new CrawlRepoFilterRecord
                    {
                        Id = Guid.NewGuid(), CrawlConfigurationId = record.Id,
                        RepositoryName = "repo", SourceProvider = "GitHub", CanonicalSourceRef = "12345", TargetBranchPatterns = ["main"]
                    }
                ];
                break;
            case "identity": filter.CanonicalSourceRef = "67890"; break;
            case "name": filter.RepositoryName = "other"; break;
            case "scope": record.OrganizationUrl = "https://other.example.test"; break;
            case "project": record.ProjectId = "other"; break;
            case "provider": record.Provider = ScmProvider.GitLab; break;
        }

        await this._dbContext.SaveChangesAsync();
        this._dbContext.ChangeTracker.Clear();

        Assert.False(await this._repo.UpdateReviewTargetPolicyAsync(created, this._clientId, ["main"], []));
        Assert.Equal(["main"], Assert.Single((await this._repo.GetByIdAsync(created.Id))!.RepoFilters).TargetBranchPatterns);
    }

    [Fact]
    public async Task DestinationPolicy_ConcurrentConditionalEditsAllowOnlyOneWriter()
    {
        var created = await this._repo.AddReviewTargetAsync(
            this._clientId, ScmProvider.GitHub, "https://github.example.test", "owner", "12345", "repo",
            targetBranchPatterns: ["main"]);

        async Task<bool> EditAsync(string pattern)
        {
            await using var context = new MeisterProPRDbContext(
                new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(fixture.ConnectionString, o => o.UseVector()).Options);
            return await new CrawlConfigurationRepository(context).UpdateReviewTargetPolicyAsync(created, this._clientId, ["main"], [pattern]);
        }

        var results = await Task.WhenAll(EditAsync("release/*"), EditAsync("develop"));

        Assert.Single(results, success => success);
        this._dbContext.ChangeTracker.Clear();
        var stored = (await this._repo.GetByIdAsync(created.Id))!;
        Assert.Contains(Assert.Single(stored.RepoFilters[0].TargetBranchPatterns), new[] { "release/*", "develop" });
        Assert.False(stored.IsActive);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("project")]
    public async Task DestinationPolicy_IdentityChangedWhilePolicyWaitsForLocksIsRefused(string change)
    {
        var created = await this._repo.AddReviewTargetAsync(
            this._clientId, ScmProvider.GitHub, "https://github.example.test", "owner", "12345", "repo", targetBranchPatterns: ["main"]);
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(fixture.ConnectionString, o => o.UseVector()).Options;
        await using var filterContext = new MeisterProPRDbContext(options);
        await using var filterTransaction = await filterContext.Database.BeginTransactionAsync();
        await filterContext.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM crawl_repo_filters WHERE id = {created.RepoFilters[0].Id} FOR UPDATE");
        await using var identityContext = new MeisterProPRDbContext(options);
        await using var identityTransaction = await identityContext.Database.BeginTransactionAsync();
        await identityContext.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM crawl_configurations WHERE id = {created.Id} FOR UPDATE");
        await using var policyContext = new MeisterProPRDbContext(options);
        await policyContext.Database.OpenConnectionAsync();
        await using var pidCommand = new NpgsqlCommand("SELECT pg_backend_pid()", (NpgsqlConnection)policyContext.Database.GetDbConnection());
        var policyPid = (int)(await pidCommand.ExecuteScalarAsync())!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var edit = new CrawlConfigurationRepository(policyContext).UpdateReviewTargetPolicyAsync(created, this._clientId, ["main"], [], deadline.Token);
        try
        {
            await using var monitor = new NpgsqlConnection(fixture.ConnectionString);
            await monitor.OpenAsync(deadline.Token);
            await using var waiting = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE pid = @pid AND wait_event_type = 'Lock')", monitor);
            waiting.Parameters.AddWithValue("pid", policyPid);
            while (!(bool)(await waiting.ExecuteScalarAsync(deadline.Token))!)
            {
                await Task.Delay(10, deadline.Token);
            }

            if (change == "scope")
            {
                await identityContext.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE crawl_configurations SET organization_url = 'https://other.example.test' WHERE id = {created.Id}", deadline.Token);
            }
            else
            {
                await identityContext.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE crawl_configurations SET project_id = 'other' WHERE id = {created.Id}", deadline.Token);
            }

            await identityTransaction.CommitAsync(deadline.Token);
            await filterTransaction.CommitAsync(deadline.Token);
            Assert.False(await edit);
            this._dbContext.ChangeTracker.Clear();
            Assert.Equal(["main"], Assert.Single((await this._repo.GetByIdAsync(created.Id))!.RepoFilters).TargetBranchPatterns);
        }
        finally
        {
            await identityTransaction.DisposeAsync();
            await filterTransaction.DisposeAsync();
            await edit;
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task DestinationPolicy_SameContextReadbackReturnsPersistedPolicyAndConcurrentActivation(bool initialActivation, bool currentActivation)
    {
        var created = await this._repo.AddReviewTargetAsync(
            this._clientId, ScmProvider.GitHub, "https://github.example.test", "owner", "12345", "repo", targetBranchPatterns: ["main"]);
        await this._repo.SetActiveAsync(created.Id, this._clientId, initialActivation);
        var authorized = Assert.Single(await this._repo.GetByClientAsync(this._clientId));
        Assert.Equal(initialActivation, authorized.IsActive);
        await using var concurrentContext = new MeisterProPRDbContext(
            new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(fixture.ConnectionString, o => o.UseVector()).Options);
        await concurrentContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE crawl_configurations SET is_active = {currentActivation} WHERE id = {created.Id}");
        Assert.True(await this._repo.UpdateReviewTargetPolicyAsync(authorized, this._clientId, ["main"], ["release/*"]));

        var current = (await this._repo.GetReviewTargetPolicySnapshotAsync(created.Id, this._clientId))!;
        Assert.Equal(["release/*"], Assert.Single(current.RepoFilters).TargetBranchPatterns);
        Assert.Equal(currentActivation, current.IsActive);
        Assert.Equal(created.RepoFilters[0].Id, current.RepoFilters[0].Id);
        Assert.Null(await this._repo.GetReviewTargetPolicySnapshotAsync(created.Id, this._otherClientId));
    }

    [Fact]
    public async Task AddReviewTargetAsync_DistinctConcurrentRepositoriesCoexistWithLegacyAndGenericFilters()
    {
        var legacy = await this.SeedConfig("https://github.example.test", "owner");
        legacy.RepoFilters.Add(
            new CrawlRepoFilterRecord
                { Id = Guid.NewGuid(), RepositoryName = "legacy", SourceProvider = "GitHub", CanonicalSourceRef = "legacy-id", TargetBranchPatterns = [] });
        legacy.RepoFilters.Add(
            new CrawlRepoFilterRecord
                { Id = Guid.NewGuid(), RepositoryName = "other", SourceProvider = "GitHub", CanonicalSourceRef = "other-id", TargetBranchPatterns = [] });
        await this._dbContext.SaveChangesAsync();

        async Task<CrawlConfigurationDto> CreateAsync(string id)
        {
            await using var context = new MeisterProPRDbContext(
                new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(fixture.ConnectionString, o => o.UseVector()).Options);
            return await new CrawlConfigurationRepository(context).AddReviewTargetAsync(
                this._clientId, ScmProvider.GitHub, "https://github.example.test", "owner", id, id);
        }

        var targets = await Task.WhenAll(CreateAsync("repo-a"), CreateAsync("repo-b"));

        Assert.NotEqual(targets[0].Id, targets[1].Id);
        Assert.All(targets, target => Assert.False(target.IsActive));
        var stored = await this._repo.GetByClientAsync(this._clientId);
        Assert.Equal(3, stored.Count);
        Assert.Equal(2, stored.Single(config => config.Id == legacy.Id).RepoFilters.Count);
    }

    [Fact]
    public async Task AddReviewTargetAsync_SameConcurrentRepositoryHasOnePersistedIdentity()
    {
        async Task<bool> CreateAsync()
        {
            await using var context = new MeisterProPRDbContext(
                new DbContextOptionsBuilder<MeisterProPRDbContext>().UseNpgsql(fixture.ConnectionString, o => o.UseVector()).Options);
            try
            {
                await new CrawlConfigurationRepository(context).AddReviewTargetAsync(
                    this._clientId, ScmProvider.GitHub, "https://github.example.test", "owner", "repo-a", "repo-a");
                return true;
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
            {
                return false;
            }
        }

        var results = await Task.WhenAll(CreateAsync(), CreateAsync());

        Assert.Single(results, created => created);
        Assert.Single(await this._repo.GetByClientAsync(this._clientId));
    }

    [Fact]
    public async Task UpdateRepoFiltersAsync_RepositorySpecificParentTracksCanonicalReplacement()
    {
        var target = await this.SeedConfig(repositoryId: "old-id");
        await this._repo.UpdateRepoFiltersAsync(
            target.Id, [new CrawlRepoFilterDto(Guid.NewGuid(), "new", [], new CanonicalSourceReferenceDto("AzureDevOps", "new-id"))]);

        var stored = await this._dbContext.CrawlConfigurations.AsNoTracking().SingleAsync(config => config.Id == target.Id);
        Assert.Equal("new-id", stored.RepositoryId);
    }

    [Fact]
    public async Task UpdateRepoFiltersAsync_RepositorySpecificParentRejectsGenericConversionBeforeWrites()
    {
        var target = await this.SeedConfig(repositoryId: "old-id");
        await Assert.ThrowsAsync<InvalidOperationException>(() => this._repo.UpdateRepoFiltersAsync(target.Id, []));

        var stored = await this._dbContext.CrawlConfigurations.AsNoTracking().SingleAsync(config => config.Id == target.Id);
        Assert.Equal("old-id", stored.RepositoryId);
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
            0.2f);

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
            0.45f,
            true);

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
            0.45f);

        var updated = await this._repo.UpdateAsync(
            created.Id,
            null,
            null,
            this._clientId,
            CancellationToken.None,
            null,
            true);

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

/// <summary>Verifies current-state update outcomes against isolated PostgreSQL storage without skipping unavailable fixtures.</summary>
[Collection("PostgresIntegration2")]
public sealed class CrawlConfigurationUpdateOutcomeTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private readonly Guid _clientId = Guid.NewGuid();
    private MeisterProPRDbContext _db = null!;
    private CrawlConfigurationRepository _repository = null!;

    public async Task InitializeAsync()
    {
        Assert.True(fixture.IsAvailable, "An isolated PostgreSQL fixture is required for update outcome regressions.");
        this._db = this.CreateContext();
        this._db.Clients.Add(
            new ClientRecord
            {
                Id = this._clientId,
                TenantId = TenantCatalog.SystemTenantId,
                DisplayName = "Update outcome test client",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        await this._db.SaveChangesAsync();
        this._repository = new CrawlConfigurationRepository(this._db);
    }

    public async Task DisposeAsync()
    {
        if (this._db is null)
        {
            return;
        }

        this._db.ChangeTracker.Clear();
        await this._db.CrawlConfigurations.Where(config => config.ClientId == this._clientId).ExecuteDeleteAsync();
        await this._db.Clients.Where(client => client.Id == this._clientId).ExecuteDeleteAsync();
        await this._db.DisposeAsync();
    }

    [Theory]
    [InlineData(ReviewTargetLifecycle.Disabled, false)]
    [InlineData(ReviewTargetLifecycle.Removed, false)]
    [InlineData(ReviewTargetLifecycle.Removed, true)]
    public async Task UpdateWithResultAsync_CurrentStateConflict_PreservesAllStoredFields(ReviewTargetLifecycle lifecycle, bool replaceFilters)
    {
        var initial = await this.CreateTargetAsync();
        Assert.Equal(ReviewTargetLifecycle.Enabled, initial.ReviewTargetLifecycle);
        await using (var concurrent = this.CreateContext())
        {
            await concurrent.CrawlConfigurations.Where(config => config.Id == initial.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(config => config.ReviewTargetLifecycle, lifecycle));
        }

        var before = await this.ReadTargetAsync(initial.Id);
        var result = await this._repository.UpdateWithResultAsync(
            initial.Id, 120, replaceFilters ? null : true, this._clientId,
            reviewTemperature: 0.75f, shouldUpdateReviewTemperature: true,
            repoFilters: replaceFilters ? [] : null);

        Assert.Equal(CrawlConfigurationUpdateResult.Conflict, result);
        Assert.False(
            await this._repository.UpdateAsync(
                initial.Id, 120, replaceFilters ? null : true, this._clientId,
                reviewTemperature: 0.75f, shouldUpdateReviewTemperature: true,
                repoFilters: replaceFilters ? [] : null));
        if (replaceFilters)
        {
            Assert.False(await this._repository.UpdateRepoFiltersAsync(initial.Id, []));
        }

        AssertStoredStateEqual(before, await this.ReadTargetAsync(initial.Id));
    }

    [Fact]
    public async Task UpdateWithResultAsync_ForeignOwnerAndMissingIdentity_ReturnNotFoundWithoutMutation()
    {
        var target = await this.CreateTargetAsync();
        var before = await this.ReadTargetAsync(target.Id);
        var foreignClientId = Guid.NewGuid();

        Assert.Equal(
            CrawlConfigurationUpdateResult.NotFound,
            await this._repository.UpdateWithResultAsync(target.Id, 120, true, foreignClientId));
        Assert.Equal(
            CrawlConfigurationUpdateResult.NotFound,
            await this._repository.UpdateWithResultAsync(Guid.NewGuid(), 120, true, this._clientId));
        Assert.False(await this._repository.UpdateAsync(target.Id, 120, true, foreignClientId));
        Assert.False(await this._repository.UpdateAsync(Guid.NewGuid(), 120, true, this._clientId));
        AssertStoredStateEqual(before, await this.ReadTargetAsync(target.Id));
    }

    [Fact]
    public async Task UpdateWithResultAsync_RemovedSettingsEditAndLegacyUpdate_PreserveLifecycleAndPolicy()
    {
        var target = await this.CreateTargetAsync();
        await this._db.CrawlConfigurations.Where(config => config.Id == target.Id).ExecuteUpdateAsync(setters =>
            setters.SetProperty(config => config.ReviewTargetLifecycle, ReviewTargetLifecycle.Removed));
        var before = await this.ReadTargetAsync(target.Id);

        Assert.Equal(
            CrawlConfigurationUpdateResult.Updated,
            await this._repository.UpdateWithResultAsync(
                target.Id, 120, false, this._clientId,
                reviewTemperature: 0.25f, shouldUpdateReviewTemperature: true));
        var updated = await this.ReadTargetAsync(target.Id);
        Assert.Equal(120, updated.CrawlIntervalSeconds);
        Assert.Equal(0.25f, updated.ReviewTemperature);
        Assert.False(updated.IsActive);
        Assert.Equal(before.ReviewTargetRevision + 1, updated.ReviewTargetRevision);
        Assert.Equal(ReviewTargetLifecycle.Removed, updated.ReviewTargetLifecycle);
        Assert.Equal(before.RepositoryId, updated.RepositoryId);
        Assert.Equal(before.RepoFilters.Single().CanonicalSourceRef, updated.RepoFilters.Single().CanonicalSourceRef);
        Assert.Equal(before.RepoFilters.Single().TargetBranchPatterns, updated.RepoFilters.Single().TargetBranchPatterns);

        Assert.True(await this._repository.UpdateAsync(target.Id, 180, null, this._clientId));
        var legacyUpdated = await this.ReadTargetAsync(target.Id);
        Assert.Equal(180, legacyUpdated.CrawlIntervalSeconds);
        Assert.Equal(updated.ReviewTargetRevision + 1, legacyUpdated.ReviewTargetRevision);
        Assert.Equal(ReviewTargetLifecycle.Removed, legacyUpdated.ReviewTargetLifecycle);
        Assert.False(legacyUpdated.IsActive);
        Assert.Equal(updated.RepoFilters.Single().CanonicalSourceRef, legacyUpdated.RepoFilters.Single().CanonicalSourceRef);
        Assert.Equal(updated.RepoFilters.Single().TargetBranchPatterns, legacyUpdated.RepoFilters.Single().TargetBranchPatterns);
    }

    private Task<CrawlConfigurationDto> CreateTargetAsync() => this._repository.AddReviewTargetAsync(
        this._clientId, ScmProvider.GitHub, "https://github.example.test", "owner", "12345", "repo", targetBranchPatterns: ["main"]);

    private MeisterProPRDbContext CreateContext() => new(
        new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, options => options.UseVector()).Options);

    private async Task<CrawlConfigurationRecord> ReadTargetAsync(Guid targetId)
    {
        await using var read = this.CreateContext();
        return await read.CrawlConfigurations.AsNoTracking().Include(config => config.RepoFilters).SingleAsync(config => config.Id == targetId);
    }

    private static void AssertStoredStateEqual(CrawlConfigurationRecord expected, CrawlConfigurationRecord actual)
    {
        Assert.Equal(expected.ClientId, actual.ClientId);
        Assert.Equal(expected.Provider, actual.Provider);
        Assert.Equal(expected.OrganizationUrl, actual.OrganizationUrl);
        Assert.Equal(expected.ProjectId, actual.ProjectId);
        Assert.Equal(expected.RepositoryId, actual.RepositoryId);
        Assert.Equal(expected.BranchFilter, actual.BranchFilter);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.ProCursorSourceScopeMode, actual.ProCursorSourceScopeMode);
        Assert.Equal(expected.ReviewTargetLifecycle, actual.ReviewTargetLifecycle);
        Assert.Equal(expected.IsActive, actual.IsActive);
        Assert.Equal(expected.CrawlIntervalSeconds, actual.CrawlIntervalSeconds);
        Assert.Equal(expected.ReviewTemperature, actual.ReviewTemperature);
        Assert.Equal(expected.ReviewTargetRevision, actual.ReviewTargetRevision);
        var expectedFilter = Assert.Single(expected.RepoFilters);
        var actualFilter = Assert.Single(actual.RepoFilters);
        Assert.Equal(expectedFilter.Id, actualFilter.Id);
        Assert.Equal(expectedFilter.SourceProvider, actualFilter.SourceProvider);
        Assert.Equal(expectedFilter.CanonicalSourceRef, actualFilter.CanonicalSourceRef);
        Assert.Equal(expectedFilter.RepositoryName, actualFilter.RepositoryName);
        Assert.Equal(expectedFilter.DisplayName, actualFilter.DisplayName);
        Assert.Equal(expectedFilter.TargetBranchPatterns, actualFilter.TargetBranchPatterns);
    }
}
