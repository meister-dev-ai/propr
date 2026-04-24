// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.Clients;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterProPR.Application.Tests.Features.Clients;

public sealed class ClientsModuleTests
{
    [Fact]
    public async Task PatchAsync_WithEmptyCustomSystemMessage_ClearsStoredMessage()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        db.Clients.Add(
            new ClientRecord
            {
                Id = clientId,
                DisplayName = "Feature Client",
                IsActive = true,
                CustomSystemMessage = "keep me",
                CreatedAt = DateTimeOffset.UtcNow,
            });
        await db.SaveChangesAsync();

        var sut = new ClientAdminService(db);

        var result = await sut.PatchAsync(clientId, null, null, null, string.Empty);

        Assert.NotNull(result);
        Assert.Null(result!.CustomSystemMessage);
        Assert.Null(
            await db.Clients.Where(record => record.Id == clientId)
                .Select(record => record.CustomSystemMessage)
                .SingleAsync());
    }

    [Fact]
    public void AddClientsModule_WithDatabaseConnectionString_RegistersProviderReadinessServices()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["DB_CONNECTION_STRING"] = "Host=localhost;Database=meisterpropr;Username=test;Password=test",
                })
            .Build();

        services.AddDataProtection();
        services.AddClientsModule(configuration);

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IProviderReadinessProfileCatalog));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IProviderReadinessEvaluator));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IProviderOperationalStatusService));
    }

    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase($"ClientsModuleTests_{Guid.NewGuid()}")
            .Options;

        return new MeisterProPRDbContext(options);
    }
}
