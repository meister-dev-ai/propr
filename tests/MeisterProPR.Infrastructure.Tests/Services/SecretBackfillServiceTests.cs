// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Infrastructure.Tests.Services;

public sealed class SecretBackfillServiceTests
{
    private static ISecretProtectionCodec CreateCodec()
    {
        var keysDirectory = Path.Combine(
            Path.GetTempPath(),
            $"MeisterProPR.SecretBackfillServiceTests.{Guid.NewGuid():N}");
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
            .UseInMemoryDatabase($"TestDb_SecretBackfill_{Guid.NewGuid()}")
            .Options;
        return new MeisterProPRDbContext(options);
    }

    [Fact]
    public async Task BackfillAsync_PlaintextApiKeys_ProtectsLegacyAiConnectionValues()
    {
        await using var db = CreateContext();
        var codec = CreateCodec();
        var clientId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();

        db.Clients.Add(
            new ClientRecord
            {
                Id = clientId,
                DisplayName = "Legacy Client",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });

        db.AiConnections.Add(
            new AiConnectionRecord
            {
                Id = connectionId,
                ClientId = clientId,
                DisplayName = "Legacy Connection",
                EndpointUrl = "https://fake.openai.azure.com/",
                Models = ["gpt-4o"],
                ApiKey = "legacy-ai-key",
                CreatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();

        var sut = new SecretBackfillService(db, codec);

        await sut.BackfillAsync(CancellationToken.None);

        var connection = await db.AiConnections.AsNoTracking().FirstAsync(c => c.Id == connectionId);

        Assert.NotEqual("legacy-ai-key", connection.ApiKey);
        Assert.Equal("legacy-ai-key", codec.Unprotect(connection.ApiKey!, "AiConnectionApiKey"));
    }

    [Fact]
    public async Task BackfillAsync_AlreadyProtectedApiKeys_RemainsIdempotent()
    {
        await using var db = CreateContext();
        var codec = CreateCodec();
        var clientId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var protectedApiKey = codec.Protect("protected-ai-key", "AiConnectionApiKey");

        db.Clients.Add(
            new ClientRecord
            {
                Id = clientId,
                DisplayName = "Protected Client",
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
            });

        db.AiConnections.Add(
            new AiConnectionRecord
            {
                Id = connectionId,
                ClientId = clientId,
                DisplayName = "Protected Connection",
                EndpointUrl = "https://fake.openai.azure.com/",
                Models = ["gpt-4o"],
                ApiKey = protectedApiKey,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();

        var sut = new SecretBackfillService(db, codec);

        await sut.BackfillAsync(CancellationToken.None);

        var connection = await db.AiConnections.AsNoTracking().FirstAsync(c => c.Id == connectionId);

        Assert.Equal(protectedApiKey, connection.ApiKey);
    }
}
