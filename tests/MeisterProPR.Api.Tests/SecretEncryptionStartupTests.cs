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
        => Task.CompletedTask;

    public Task InitializeAsync()
        => Task.CompletedTask;

    [SkippableFact]
    public async Task Startup_PlaintextSecretsInDatabase_UpgradesRowsBeforeServingRequests()
    {
        fixture.SkipIfUnavailable();

        var clientId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var keysDirectory = Path.Combine(Path.GetTempPath(), $"MeisterProPR.SecretEncryptionStartupTests.{Guid.NewGuid():N}");

        Directory.CreateDirectory(keysDirectory);

        try
        {
            var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
                .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
                .Options;

            await using (var db = new MeisterProPRDbContext(options))
            {
                db.Clients.Add(new ClientRecord
                {
                    Id = clientId,
                    DisplayName = $"Legacy Startup Client {clientId:N}",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                    AdoTenantId = "tenant-id",
                    AdoClientId = "client-id",
                    AdoClientSecret = "legacy-ado-secret",
                });

                db.AiConnections.Add(new AiConnectionRecord
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
            var credentialRepository = scope.ServiceProvider.GetRequiredService<IClientAdoCredentialRepository>();
            var aiConnectionRepository = scope.ServiceProvider.GetRequiredService<IAiConnectionRepository>();

            var client = await runtimeDb.Clients.AsNoTracking().FirstAsync(c => c.Id == clientId);
            var connection = await runtimeDb.AiConnections.AsNoTracking().FirstAsync(c => c.Id == connectionId);
            var credentials = await credentialRepository.GetByClientIdAsync(clientId, CancellationToken.None);
            var aiConnection = await aiConnectionRepository.GetByIdAsync(connectionId, CancellationToken.None);

            Assert.NotNull(credentials);
            Assert.NotNull(aiConnection);
            Assert.NotEqual("legacy-ado-secret", client.AdoClientSecret);
            Assert.NotEqual("legacy-ai-key", connection.ApiKey);
            Assert.Equal("legacy-ado-secret", credentials.Secret);
            Assert.Equal("legacy-ai-key", aiConnection.ApiKey);
        }
        finally
        {
            if (Directory.Exists(keysDirectory))
            {
                Directory.Delete(keysDirectory, recursive: true);
            }
        }
    }
}
