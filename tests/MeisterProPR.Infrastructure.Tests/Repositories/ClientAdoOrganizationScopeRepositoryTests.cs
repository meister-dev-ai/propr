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
        db.Clients.Add(new ClientRecord
        {
            Id = id,
            DisplayName = "Test Client",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task AddAsync_StoresNormalizedOrganizationUrl()
    {
        await using var db = CreateContext();
        var clientId = await SeedClientAsync(db);
        var sut = new ClientAdoOrganizationScopeRepository(db);

        var created = await sut.AddAsync(clientId, "https://dev.azure.com/org/", "Org", CancellationToken.None);

        Assert.NotNull(created);
        Assert.Equal("https://dev.azure.com/org", created.OrganizationUrl);
        Assert.Equal(AdoOrganizationVerificationStatus.Unknown, created.VerificationStatus);
    }

    [Fact]
    public async Task UpdateVerificationAsync_PersistsVerificationStatus()
    {
        await using var db = CreateContext();
        var clientId = await SeedClientAsync(db);
        var sut = new ClientAdoOrganizationScopeRepository(db);
        var created = await sut.AddAsync(clientId, "https://dev.azure.com/org", "Org", CancellationToken.None);

        var updated = await sut.UpdateVerificationAsync(
            clientId,
            created!.Id,
            AdoOrganizationVerificationStatus.Verified,
            DateTimeOffset.UtcNow,
            null,
            CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Equal(AdoOrganizationVerificationStatus.Verified, updated.VerificationStatus);
        Assert.NotNull(updated.LastVerifiedAt);
    }

    [Fact]
    public async Task DeleteAsync_RemovesScope()
    {
        await using var db = CreateContext();
        var clientId = await SeedClientAsync(db);
        var sut = new ClientAdoOrganizationScopeRepository(db);
        var created = await sut.AddAsync(clientId, "https://dev.azure.com/org", "Org", CancellationToken.None);

        var deleted = await sut.DeleteAsync(clientId, created!.Id, CancellationToken.None);
        var remaining = await sut.GetByClientIdAsync(clientId, CancellationToken.None);

        Assert.True(deleted);
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task AddAsync_Throws_WhenNormalizedOrganizationUrlAlreadyExists()
    {
        await using var db = CreateContext();
        var clientId = await SeedClientAsync(db);
        var sut = new ClientAdoOrganizationScopeRepository(db);

        await sut.AddAsync(clientId, "https://dev.azure.com/org/", "Org", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sut.AddAsync(clientId, "https://dev.azure.com/org", "Duplicate Org", CancellationToken.None));
    }
}
