// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Services;
using MeisterProPR.Infrastructure.Tests.GitHub;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
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

    [Fact]
    public async Task AddAsync_GitHubAppInstallation_PersistsGitHubAppMetadataAndProtectedSecret()
    {
        var client = await this.SeedClientAsync();
        var privateKeyPem = GitHubAppTestHelpers.CreatePrivateKeyPem(unique: true);

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
            gitHubAppId: 123456,
            gitHubAppInstallationId: 789012,
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
    public async Task UpdateAsync_GitHubAppRotation_ReprotectsSecretAndResetsVerification()
    {
        var client = await this.SeedClientAsync();
        var originalSecret = GitHubAppTestHelpers.CreatePrivateKeyPem(unique: true);
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
            gitHubAppId: 123456,
            gitHubAppInstallationId: 789012,
            ct: CancellationToken.None);
        Assert.NotNull(created);

        await this._repository.UpdateVerificationAsync(
            client.Id,
            created!.Id,
            "verified",
            DateTimeOffset.UtcNow,
            null,
            CancellationToken.None);

        var rotatedSecret = GitHubAppTestHelpers.CreatePrivateKeyPem(unique: true);
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
            gitHubAppId: 456123,
            gitHubAppInstallationId: 654321,
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
            GitHubAppTestHelpers.CreatePrivateKeyPem(unique: true),
            true,
            gitHubAppId: 123456,
            gitHubAppInstallationId: 789012,
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
            gitHubAppId: null,
            gitHubAppInstallationId: null,
            ct: CancellationToken.None);

        Assert.NotNull(updated);
        Assert.Null(updated!.GitHubAppId);
        Assert.Null(updated.GitHubAppInstallationId);

        var record = await this._dbContext.ClientScmConnections.SingleAsync(connection => connection.Id == created.Id);
        Assert.Null(record.GitHubAppId);
        Assert.Null(record.GitHubAppInstallationId);
        Assert.Equal(ScmAuthenticationKind.PersonalAccessToken, record.AuthenticationKind);
    }

    public void Dispose()
    {
        this._dbContext.Dispose();
    }

    private async Task<ClientRecord> SeedClientAsync()
    {
        var client = new ClientRecord
        {
            Id = Guid.NewGuid(),
            TenantId = TenantCatalog.SystemTenantId,
            DisplayName = "Repository Test Client",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        this._dbContext.Clients.Add(client);
        await this._dbContext.SaveChangesAsync();
        return client;
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
}
