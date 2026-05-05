// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Services;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Tests for <see cref="DbClientRegistry" /> reviewer identity lookups.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class ClientRegistryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private ClientScmConnectionRepository _connectionRepository = null!;
    private MeisterProPRDbContext _dbContext = null!;
    private DbClientRegistry _registry = null!;
    private ClientReviewerIdentityRepository _reviewerIdentityRepository = null!;
    private readonly List<Guid> _seededClientIds = [];

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);
        var codec = CreateCodec();
        this._connectionRepository = new ClientScmConnectionRepository(this._dbContext, codec);
        this._reviewerIdentityRepository = new ClientReviewerIdentityRepository(this._dbContext);
        this._registry = new DbClientRegistry(
            this._dbContext,
            this._connectionRepository,
            this._reviewerIdentityRepository);
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is not null)
        {
            if (this._seededClientIds.Count > 0)
            {
                await this._dbContext.ClientReviewerIdentities
                    .Where(identity => this._seededClientIds.Contains(identity.ClientId))
                    .ExecuteDeleteAsync();
                await this._dbContext.ClientScmScopes
                    .Where(scope => this._seededClientIds.Contains(scope.ClientId))
                    .ExecuteDeleteAsync();
                await this._dbContext.ClientScmConnections
                    .Where(connection => this._seededClientIds.Contains(connection.ClientId))
                    .ExecuteDeleteAsync();
                await this._dbContext.Clients
                    .Where(client => this._seededClientIds.Contains(client.Id))
                    .ExecuteDeleteAsync();
            }

            await this._dbContext.DisposeAsync();
        }
    }

    private static ISecretProtectionCodec CreateCodec()
    {
        var keysDirectory = Path.Combine(Path.GetTempPath(), $"MeisterProPR.ClientRegistryTests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(keysDirectory);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("MeisterProPR.Tests")
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));

        var provider = services.BuildServiceProvider();
        return new SecretProtectionCodec(provider.GetRequiredService<IDataProtectionProvider>());
    }

    [Fact]
    public async Task GetReviewerIdentityAsync_ActiveConnectionWithConfiguredIdentity_ReturnsReviewerIdentity()
    {
        var client = await this.SeedClientAsync();
        var connection = await this._connectionRepository.AddAsync(
            client.Id,
            ScmProvider.GitHub,
            "https://github.com",
            ScmAuthenticationKind.PersonalAccessToken,
            "GitHub",
            "ghp_test",
            true,
            CancellationToken.None);

        Assert.NotNull(connection);

        await this._reviewerIdentityRepository.UpsertAsync(
            client.Id,
            connection!.Id,
            ScmProvider.GitHub,
            "12345",
            "meister-review-bot[bot]",
            "Meister Review Bot",
            true,
            CancellationToken.None);

        var result = await this._registry.GetReviewerIdentityAsync(
            client.Id,
            new ProviderHostRef(ScmProvider.GitHub, "https://github.com"),
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("12345", result!.ExternalUserId);
        Assert.Equal("meister-review-bot[bot]", result.Login);
        Assert.Equal("Meister Review Bot", result.DisplayName);
        Assert.True(result.IsBot);
    }

    [Fact]
    public async Task GetReviewerIdentityAsync_UnknownHost_ReturnsNull()
    {
        var client = await this.SeedClientAsync();

        var result = await this._registry.GetReviewerIdentityAsync(
            client.Id,
            new ProviderHostRef(ScmProvider.GitHub, "https://github.example.com"),
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetReviewerIdentityAsync_ConnectionWithoutReviewerIdentity_ReturnsNull()
    {
        var client = await this.SeedClientAsync();
        await this._connectionRepository.AddAsync(
            client.Id,
            ScmProvider.GitHub,
            "https://github.com",
            ScmAuthenticationKind.PersonalAccessToken,
            "GitHub",
            "ghp_test",
            true,
            CancellationToken.None);

        var result = await this._registry.GetReviewerIdentityAsync(
            client.Id,
            new ProviderHostRef(ScmProvider.GitHub, "https://github.com"),
            CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetScmCommentPostingEnabledAsync_ClientWithSetting_ReturnsPersistedValue()
    {
        var client = await this.SeedClientAsync();
        client.ScmCommentPostingEnabled = false;
        await this._dbContext.SaveChangesAsync();

        var result = await this._registry.GetScmCommentPostingEnabledAsync(client.Id, CancellationToken.None);

        Assert.False(result);
    }

    [Fact]
    public async Task GetScmCommentPostingEnabledAsync_UnknownClient_DefaultsToTrue()
    {
        var result = await this._registry.GetScmCommentPostingEnabledAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.True(result);
    }

    private async Task<ClientRecord> SeedClientAsync()
    {
        var record = new ClientRecord
        {
            Id = Guid.NewGuid(),
            TenantId = TenantCatalog.SystemTenantId,
            DisplayName = "Test Client",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        this._dbContext.Clients.Add(record);
        await this._dbContext.SaveChangesAsync();
        this._seededClientIds.Add(record.Id);
        return record;
    }
}
