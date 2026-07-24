// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Tests.AI;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Integration tests for <see cref="LogicalModelMigrationBackfill" /> against a real PostgreSQL instance: legacy
///     configured-model review passes migrate onto per-client logical-model overrides, idempotently, deduped by mapping.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class LogicalModelMigrationBackfillTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private MeisterProPRDbContext _dbContext = null!;
    private IAiConnectionRepository _connections = null!;
    private ILogicalModelCatalogRepository _catalog = null!;
    private LogicalModelMigrationBackfill _backfill = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);

        var now = DateTimeOffset.UtcNow;
        this._dbContext.Tenants.Add(
            new TenantRecord
            {
                Id = this._tenantId,
                Slug = "bf-" + this._tenantId.ToString("N"),
                DisplayName = "Backfill Test Tenant",
                IsActive = true,
                LocalLoginEnabled = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
        this._dbContext.Clients.Add(
            new ClientRecord
            {
                Id = this._clientId,
                TenantId = this._tenantId,
                DisplayName = "Backfill Test Client",
                IsActive = true,
                CreatedAt = now,
            });
        await this._dbContext.SaveChangesAsync();

        this._connections = Substitute.For<IAiConnectionRepository>();
        this._catalog = new LogicalModelCatalogRepository(this._dbContext, Substitute.For<ILogicalModelCapabilityValidator>());
        this._backfill = new LogicalModelMigrationBackfill(this._dbContext, this._connections, this._catalog);
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is null)
        {
            return;
        }

        await this._dbContext.LogicalModelOverrides.Where(x => x.ClientId == this._clientId).ExecuteDeleteAsync();
        await this._dbContext.ClientPurposeLogicalModels.Where(x => x.ClientId == this._clientId).ExecuteDeleteAsync();
        await this._dbContext.ClientReviewPasses.Where(x => x.ClientId == this._clientId).ExecuteDeleteAsync();
        // Configured models + connection profiles cascade-delete with the client.
        await this._dbContext.Clients.Where(c => c.Id == this._clientId).ExecuteDeleteAsync();
        await this._dbContext.Tenants.Where(t => t.Id == this._tenantId).ExecuteDeleteAsync();
        await this._dbContext.DisposeAsync();
    }

    [Fact]
    public async Task Backfill_MigratesLegacyPasses_Idempotently_AndDedupsByMapping()
    {
        var connProfileId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        this.SeedConnectionModel(connProfileId, modelId, "gpt-4o");

        // The backfill resolves the legacy model through the connection repository (substituted here).
        var model = AiConnectionTestFactory.CreateChatModel("gpt-4o", modelId);
        var connection = AiConnectionTestFactory.CreateConnection(this._clientId, [model]);
        var binding = AiConnectionTestFactory.CreateBinding(AiPurpose.ReviewDefault, model, AiProtocolMode.Responses);
        this._connections.GetModelBindingAsync(this._clientId, modelId, Arg.Any<CancellationToken>())
            .Returns(new AiResolvedPurposeBindingDto(connection, model, binding));

        // Two legacy passes on the SAME configured model → dedup to one override.
        this._dbContext.ClientReviewPasses.AddRange(this.NewLegacyPass(0, modelId), this.NewLegacyPass(1, modelId));
        await this._dbContext.SaveChangesAsync();

        var migrated = await this._backfill.BackfillClientReviewPassesAsync(this._clientId, default);
        Assert.Equal(2, migrated);

        var passes = await this._dbContext.ClientReviewPasses.AsNoTracking()
            .Where(p => p.ClientId == this._clientId)
            .OrderBy(p => p.Ordinal)
            .ToListAsync();
        Assert.All(passes, p => Assert.Equal("migrated-gpt-4o", p.LogicalModelName));

        var role = Assert.Single(await this._catalog.GetClientOverridesAsync(this._clientId, default));
        Assert.Equal("migrated-gpt-4o", role.Name);
        Assert.Equal(connection.Id, role.ConnectionId);
        Assert.Equal(modelId, role.ConfiguredModelId);
        Assert.Equal(AiProtocolMode.Responses, role.ProtocolMode);
        Assert.Equal(AiOperationKind.Chat, role.Capability);

        // Idempotent: a second run migrates nothing and creates no further overrides.
        Assert.Equal(0, await this._backfill.BackfillClientReviewPassesAsync(this._clientId, default));
        Assert.Single(await this._catalog.GetClientOverridesAsync(this._clientId, default));
    }

    [Fact]
    public async Task BackfillPurposes_MapsUnmappedPurposes_DedupsAndHandlesEmbedding_Idempotently()
    {
        var connProfileId = Guid.NewGuid();
        var chatModelId = Guid.NewGuid();
        var embedModelId = Guid.NewGuid();
        this.SeedConnectionWithModels(connProfileId, chatModelId, embedModelId);
        await this._dbContext.SaveChangesAsync();

        var chatModel = AiConnectionTestFactory.CreateChatModel("gpt-4o", chatModelId);
        var embedModel = AiConnectionTestFactory.CreateEmbeddingModel("text-embedding-3-large", id: embedModelId);
        var connection = AiConnectionTestFactory.CreateConnection(this._clientId, [chatModel, embedModel]);

        // Two chat purposes bound to the same model collapse onto a single override; embedding gets its own with the
        // Embedding capability. Unstubbed purposes return null (no binding) and are skipped.
        this._connections.GetActiveBindingForPurposeAsync(this._clientId, AiPurpose.ReviewDefault, Arg.Any<CancellationToken>())
            .Returns(
                new AiResolvedPurposeBindingDto(
                    connection, chatModel, AiConnectionTestFactory.CreateBinding(AiPurpose.ReviewDefault, chatModel, AiProtocolMode.Responses)));
        this._connections.GetActiveBindingForPurposeAsync(this._clientId, AiPurpose.ReviewTriage, Arg.Any<CancellationToken>())
            .Returns(
                new AiResolvedPurposeBindingDto(
                    connection, chatModel, AiConnectionTestFactory.CreateBinding(AiPurpose.ReviewTriage, chatModel, AiProtocolMode.Responses)));
        this._connections.GetActiveBindingForPurposeAsync(this._clientId, AiPurpose.EmbeddingDefault, Arg.Any<CancellationToken>())
            .Returns(
                new AiResolvedPurposeBindingDto(
                    connection, embedModel, AiConnectionTestFactory.CreateBinding(AiPurpose.EmbeddingDefault, embedModel, AiProtocolMode.Embeddings)));

        var migrated = await this._backfill.BackfillClientPurposesAsync(this._clientId, default);
        Assert.Equal(3, migrated);

        var roles = await this._catalog.GetPurposeRolesAsync(this._clientId, default);
        Assert.Equal("migrated-gpt-4o", roles[AiPurpose.ReviewDefault]);
        Assert.Equal("migrated-gpt-4o", roles[AiPurpose.ReviewTriage]);
        Assert.Equal("migrated-text-embedding-3-large", roles[AiPurpose.EmbeddingDefault]);

        var overrides = await this._catalog.GetClientOverridesAsync(this._clientId, default);
        Assert.Equal(2, overrides.Count);
        var embed = Assert.Single(overrides, o => o.Capability == AiOperationKind.Embedding);
        Assert.Equal("migrated-text-embedding-3-large", embed.Name);
        Assert.Equal(embedModelId, embed.ConfiguredModelId);

        // Idempotent: a second run maps nothing new and adds no overrides.
        Assert.Equal(0, await this._backfill.BackfillClientPurposesAsync(this._clientId, default));
        Assert.Equal(2, (await this._catalog.GetClientOverridesAsync(this._clientId, default)).Count);
    }

    private void SeedConnectionWithModels(Guid connProfileId, Guid chatModelId, Guid embedModelId)
    {
        var now = DateTimeOffset.UtcNow;
        this._dbContext.AiConnectionProfiles.Add(
            new AiConnectionProfileRecord
            {
                Id = connProfileId,
                ClientId = this._clientId,
                DisplayName = "profile",
                ProviderKind = "AzureOpenAi",
                BaseUrl = "https://x",
                AuthMode = "ApiKey",
                DiscoveryMode = "ManualOnly",
                IsActive = false,
                CreatedAt = now,
                UpdatedAt = now,
                ConfiguredModels =
                [
                    new AiConfiguredModelRecord
                    {
                        Id = chatModelId,
                        ConnectionProfileId = connProfileId,
                        RemoteModelId = "gpt-4o",
                        DisplayName = "gpt-4o",
                        OperationKinds = ["Chat"],
                        SupportedProtocolModes = ["Auto"],
                        Source = "Manual",
                    },
                    new AiConfiguredModelRecord
                    {
                        Id = embedModelId,
                        ConnectionProfileId = connProfileId,
                        RemoteModelId = "text-embedding-3-large",
                        DisplayName = "text-embedding-3-large",
                        OperationKinds = ["Embedding"],
                        SupportedProtocolModes = ["Auto"],
                        Source = "Manual",
                    },
                ],
            });
    }

    private void SeedConnectionModel(Guid connProfileId, Guid modelId, string remoteModelId)
    {
        var now = DateTimeOffset.UtcNow;
        this._dbContext.AiConnectionProfiles.Add(
            new AiConnectionProfileRecord
            {
                Id = connProfileId,
                ClientId = this._clientId,
                DisplayName = "profile",
                ProviderKind = "AzureOpenAi",
                BaseUrl = "https://x",
                AuthMode = "ApiKey",
                DiscoveryMode = "ManualOnly",
                IsActive = false,
                CreatedAt = now,
                UpdatedAt = now,
                ConfiguredModels =
                [
                    new AiConfiguredModelRecord
                    {
                        Id = modelId,
                        ConnectionProfileId = connProfileId,
                        RemoteModelId = remoteModelId,
                        DisplayName = remoteModelId,
                        OperationKinds = ["Chat"],
                        SupportedProtocolModes = ["Auto"],
                        Source = "Manual",
                    },
                ],
            });
    }

    private ClientReviewPassRecord NewLegacyPass(int ordinal, Guid modelId)
    {
        return new ClientReviewPassRecord
        {
            Id = Guid.NewGuid(),
            ClientId = this._clientId,
            Ordinal = ordinal,
            ConfiguredModelId = modelId,
            LogicalModelName = null,
        };
    }
}
