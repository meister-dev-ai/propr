// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

public sealed class ConfiguredModelReasoningLevelBackfillTests
{
    private const string OpenAiKind = "meisterdev/openAi";

    private static readonly Guid ClientId = Guid.Parse("aaaaaaaa-0000-0000-0000-00000000b0f1");

    [Fact]
    public async Task BackfillAllAsync_FillsAModelWithoutLevels_FromTheCatalogOfItsClient()
    {
        await using var db = CreateContext();
        var model = AddChatModel(db, "o3", levels: null, source: null);
        await db.SaveChangesAsync();

        var changed = await Backfill(db).BackfillAllAsync(CancellationToken.None);

        Assert.Equal(1, changed);
        Assert.Equal(["low", "medium", "high"], model.ReasoningLevels!);
        Assert.Equal("Catalog", model.ReasoningLevelsSource);
    }

    // A newer catalog snapshot reaches the models whose levels came from the catalog.
    [Fact]
    public async Task BackfillAllAsync_RefreshesLevelsThatCameFromTheCatalog()
    {
        await using var db = CreateContext();
        var model = AddChatModel(db, "o3", ["low"], "Catalog");
        await db.SaveChangesAsync();

        await Backfill(db).BackfillAllAsync(CancellationToken.None);

        Assert.Equal(["low", "medium", "high"], model.ReasoningLevels!);
    }

    [Theory]
    [InlineData("Operator")]
    [InlineData("Provider")]
    public async Task BackfillAllAsync_LeavesLevelsAnOperatorEnteredOrTheProviderReported(string source)
    {
        await using var db = CreateContext();
        var model = AddChatModel(db, "o3", ["max"], source);
        await db.SaveChangesAsync();

        var changed = await Backfill(db).BackfillAllAsync(CancellationToken.None);

        Assert.Equal(0, changed);
        Assert.Equal(["max"], model.ReasoningLevels!);
        Assert.Equal(source, model.ReasoningLevelsSource);
    }

    [Fact]
    public async Task BackfillAllAsync_LeavesAModelThatNoSourceKnows_AndAnEmbeddingModel()
    {
        await using var db = CreateContext();
        var unknown = AddChatModel(db, "private-model", levels: null, source: null);
        var embedding = AddChatModel(db, "o3", levels: null, source: null, operationKind: "Embedding");
        await db.SaveChangesAsync();

        var changed = await Backfill(db).BackfillAllAsync(CancellationToken.None);

        Assert.Equal(0, changed);
        Assert.Null(unknown.ReasoningLevels);
        Assert.Null(embedding.ReasoningLevels);
    }

    // The migration gives a model that accepted the effort none the default level none. Without a source for its
    // levels, the list stays unknown, so every configured level reaches the model as configured.
    [Fact]
    public async Task BackfillAllAsync_LeavesTheLevelsUnknown_ForAModelWithADefaultLevelThatNoSourceKnows()
    {
        await using var db = CreateContext();
        var unknown = AddChatModel(db, "private-model", levels: null, source: null);
        unknown.DefaultReasoningLevel = "none";
        await db.SaveChangesAsync();

        var changed = await Backfill(db).BackfillAllAsync(CancellationToken.None);

        Assert.Equal(0, changed);
        Assert.Null(unknown.ReasoningLevels);
        Assert.Null(unknown.ReasoningLevelsSource);
    }

    [Fact]
    public async Task BackfillAllAsync_AddsTheDefaultLevel_ToCatalogLevelsThatLackIt()
    {
        await using var db = CreateContext();
        var model = AddChatModel(db, "o3", levels: null, source: null);
        model.DefaultReasoningLevel = "none";
        await db.SaveChangesAsync();

        await Backfill(db).BackfillAllAsync(CancellationToken.None);

        Assert.Equal(["none", "low", "medium", "high"], model.ReasoningLevels!);
        Assert.Equal("Catalog", model.ReasoningLevelsSource);
    }

    // Running it again changes nothing that is already current.
    [Fact]
    public async Task BackfillAllAsync_ChangesNothing_WhenTheLevelsAreCurrent()
    {
        await using var db = CreateContext();
        AddChatModel(db, "o3", levels: null, source: null);
        await db.SaveChangesAsync();
        await Backfill(db).BackfillAllAsync(CancellationToken.None);

        Assert.Equal(0, await Backfill(db).BackfillAllAsync(CancellationToken.None));
    }

    private static ConfiguredModelReasoningLevelBackfill Backfill(MeisterProPRDbContext db)
    {
        var catalog = Substitute.For<IModelCatalogRepository>();
        catalog.GetEffectiveForClientAsync(ClientId, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns([CatalogEntry("openai", "o3", ["low", "medium", "high"])]);
        // No driver serves the family here, so the catalog's levels apply unchanged.
        var drivers = Substitute.For<IAiProviderDriverRegistry>();
        drivers.IsRegistered(Arg.Any<string?>()).Returns(false);
        return new ConfiguredModelReasoningLevelBackfill(db, catalog, drivers);
    }

    private static AiConfiguredModelRecord AddChatModel(
        MeisterProPRDbContext db,
        string remoteModelId,
        string[]? levels,
        string? source,
        string operationKind = "Chat")
    {
        var connection = db.AiConnectionProfiles.Local.FirstOrDefault()
                         ?? db.AiConnectionProfiles.Add(
                             new AiConnectionProfileRecord
                             {
                                 Id = Guid.NewGuid(),
                                 ClientId = ClientId,
                                 DisplayName = "OpenAI",
                                 ProviderKind = OpenAiKind,
                                 BaseUrl = "https://api.openai.example/v1",
                                 AuthMode = "ApiKey",
                             }).Entity;
        var model = new AiConfiguredModelRecord
        {
            Id = Guid.NewGuid(),
            ConnectionProfileId = connection.Id,
            RemoteModelId = remoteModelId,
            DisplayName = remoteModelId,
            OperationKinds = [operationKind],
            SupportedProtocolModes = ["auto"],
            Source = "Manual",
            ReasoningLevels = levels,
            ReasoningLevelsSource = source,
        };
        db.AiConfiguredModels.Add(model);
        return model;
    }

    private static AiModelCatalogEntryDto CatalogEntry(string providerId, string remoteModelId, IReadOnlyList<string> levels)
    {
        return new AiModelCatalogEntryDto(
            providerId, providerId, remoteModelId, remoteModelId, null, true, true, true, false, null, null, null,
            null, null, null, null, false, null, AiModelCatalogLayer.Global, levels);
    }

    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new MeisterProPRDbContext(options);
    }
}
