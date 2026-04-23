// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Api.Tests.Fixtures;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Api.Tests;

[Collection("PostgresApiIntegration")]
public sealed class SecretEncryptionStartupTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    public Task InitializeAsync()
    {
        return Task.CompletedTask;
    }

    [SkippableFact]
    public async Task Startup_PlaintextAiConnectionSecretsInDatabase_UpgradesRowsBeforeServingRequests()
    {
        fixture.SkipIfUnavailable();

        var clientId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var keysDirectory = Path.Combine(
            Path.GetTempPath(),
            $"MeisterProPR.SecretEncryptionStartupTests.{Guid.NewGuid():N}");

        Directory.CreateDirectory(keysDirectory);

        try
        {
            var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
                .Options;

            await using (var db = new MeisterProPRDbContext(options))
            {
                db.Clients.Add(
                    new ClientRecord
                    {
                        Id = clientId,
                        DisplayName = $"Legacy Startup Client {clientId:N}",
                        IsActive = true,
                        CreatedAt = DateTimeOffset.UtcNow,
                    });

                db.AiConnections.Add(
                    new AiConnectionRecord
                    {
                        Id = connectionId,
                        ClientId = clientId,
                        DisplayName = $"Legacy Startup Connection {connectionId:N}",
                        EndpointUrl = "https://fake.openai.azure.com/",
                        Models = ["gpt-4o"],
                        ApiKey = "legacy-ai-key",
                        CreatedAt = DateTimeOffset.UtcNow,
                    });

                await db.SaveChangesAsync();
            }

            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Testing");
                    builder.UseSetting("MEISTER_DISABLE_HOSTED_SERVICES", "true");
                    builder.UseSetting("DB_CONNECTION_STRING", fixture.ConnectionString);
                    builder.UseSetting("MEISTER_DATA_PROTECTION_KEYS_PATH", keysDirectory);
                    builder.UseSetting("MEISTER_JWT_SECRET", "test-jwt-secret-at-least-32-chars-ok!!");
                    builder.UseSetting("MEISTER_BOOTSTRAP_ADMIN_USER", "testadmin");
                    builder.UseSetting("MEISTER_BOOTSTRAP_ADMIN_PASSWORD", "TestAdminPass1!");
                    builder.UseSetting("ADO_STUB_PR", "true");
                    builder.UseSetting("ADO_SKIP_TOKEN_VALIDATION", "true");
                });

            _ = factory.CreateClient();

            using var scope = factory.Services.CreateScope();
            var runtimeDb = scope.ServiceProvider.GetRequiredService<MeisterProPRDbContext>();
            var aiConnectionRepository = scope.ServiceProvider.GetRequiredService<IAiConnectionRepository>();

            var connection = await runtimeDb.AiConnections.AsNoTracking().FirstAsync(c => c.Id == connectionId);
            var aiConnection = await aiConnectionRepository.GetByIdAsync(connectionId, CancellationToken.None);

            Assert.NotNull(aiConnection);
            Assert.NotEqual("legacy-ai-key", connection.ApiKey);
            Assert.Equal("legacy-ai-key", aiConnection.Secret);
        }
        finally
        {
            if (Directory.Exists(keysDirectory))
            {
                Directory.Delete(keysDirectory, true);
            }
        }
    }
}
