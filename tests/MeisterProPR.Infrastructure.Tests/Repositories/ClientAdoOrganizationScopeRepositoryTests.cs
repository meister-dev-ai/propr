// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

public sealed class ClientAdoOrganizationScopeRepositoryTests
{
    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"TestDb_AdoScopes_{Guid.NewGuid()}")
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

    private static async Task<Guid> SeedAzureConnectionAsync(MeisterProPRDbContext db, Guid clientId)
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
                OAuthTenantId = "contoso.onmicrosoft.com",
                OAuthClientId = "11111111-1111-1111-1111-111111111111",
                DisplayName = "Azure DevOps",
                EncryptedSecretMaterial = "protected-secret",
                VerificationStatus = "unknown",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();
        return connectionId;
    }

    private static async Task<Guid> SeedAzureScopeAsync(
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
                VerificationStatus = "unknown",
                IsEnabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();
        return scopeId;
    }

    [Fact]
    public async Task GetByClientIdAsync_ProjectsSharedAzureProviderScopes()
    {
        await using var db = CreateContext();
        var clientId = await SeedClientAsync(db);
        var connectionId = await SeedAzureConnectionAsync(db, clientId);
        await SeedAzureScopeAsync(db, clientId, connectionId, "https://dev.azure.com/org");
        var sut = new ClientAdoOrganizationScopeRepository(db);

        var scopes = await sut.GetByClientIdAsync(clientId, CancellationToken.None);

        var created = Assert.Single(scopes);
        Assert.Equal("https://dev.azure.com/org", created.OrganizationUrl);
        Assert.Equal(AdoOrganizationVerificationStatus.Unknown, created.VerificationStatus);
    }

    [Fact]
    public async Task UpdateVerificationAsync_PersistsVerificationStatus()
    {
        await using var db = CreateContext();
        var clientId = await SeedClientAsync(db);
        var connectionId = await SeedAzureConnectionAsync(db, clientId);
        var scopeId = await SeedAzureScopeAsync(db, clientId, connectionId, "https://dev.azure.com/org");
        var sut = new ClientAdoOrganizationScopeRepository(db);

        var updated = await sut.UpdateVerificationAsync(
            clientId,
            scopeId,
            AdoOrganizationVerificationStatus.Verified,
            DateTimeOffset.UtcNow,
            null,
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(AdoOrganizationVerificationStatus.Verified, updated.VerificationStatus);
        Assert.NotNull(updated.LastVerifiedAt);
    }

    [Fact]
    public async Task AddAsync_ThrowsNotSupported()
    {
        await using var db = CreateContext();
        var clientId = await SeedClientAsync(db);
        var sut = new ClientAdoOrganizationScopeRepository(db);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            sut.AddAsync(clientId, "https://dev.azure.com/org", "Org", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteAsync_ThrowsNotSupported()
    {
        await using var db = CreateContext();
        var clientId = await SeedClientAsync(db);
        var sut = new ClientAdoOrganizationScopeRepository(db);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            sut.DeleteAsync(clientId, Guid.NewGuid(), CancellationToken.None));
    }
}
