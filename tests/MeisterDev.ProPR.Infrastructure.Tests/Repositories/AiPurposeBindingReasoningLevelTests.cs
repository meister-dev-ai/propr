// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Clients.Contracts;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     The reasoning level of a workspace slot: the client model selection stores it on the bindings of the slot's
///     purposes, and a save of the connection keeps it while a binding keeps its model.
/// </summary>
public sealed class AiPurposeBindingReasoningLevelTests
{
    private static readonly AiPurpose[] DefaultPurposes =
    [
        AiPurpose.ReviewDefault, AiPurpose.ReviewTriage, AiPurpose.ReviewVerification,
        AiPurpose.ReviewLowEffort, AiPurpose.ReviewMediumEffort, AiPurpose.MemoryReconsideration,
    ];

    [Fact]
    public async Task SelectPurposesAsync_StoresTheLevelOfEachChatSlotOnTheBindingsOfItsPurposes()
    {
        await using var db = CreateContext();
        var (clientId, profile) = await SeedAsync(db, ["low", "medium", "high"]);
        var repository = AiConnectionRepositoryTests.CreateRepository(db);
        var chat = Chat(profile);

        var result = await repository.SelectPurposesAsync(
            clientId,
            new(new(profile.Id, chat.Id, "medium"), new(profile.Id, chat.Id, "high"), new(profile.Id, Embedding(profile).Id)));

        Assert.True(result.Applied, result.Error);
        var bindings = (await repository.GetByIdAsync(profile.Id))!.PurposeBindings;
        Assert.All(DefaultPurposes, purpose => Assert.Equal("medium", bindings.Single(binding => binding.Purpose == purpose).ReasoningLevel));
        Assert.Equal("high", bindings.Single(binding => binding.Purpose == AiPurpose.ReviewHighEffort).ReasoningLevel);
        Assert.Null(bindings.Single(binding => binding.Purpose == AiPurpose.EmbeddingDefault).ReasoningLevel);
        var resolved = await repository.GetActiveBindingForPurposeAsync(clientId, AiPurpose.ReviewHighEffort);
        Assert.Equal("high", resolved!.Binding.ReasoningLevel);
    }

    // A caller that states no level, such as a console without the field, leaves the level of a slot alone while the
    // slot keeps its model. Another model may not accept the level, so a slot that moves loses it.
    [Fact]
    public async Task SelectPurposesAsync_KeepsTheLevelOfASlotThatKeepsItsModelAndDropsItOnAnotherModel()
    {
        await using var db = CreateContext();
        var (clientId, profile) = await SeedAsync(db, ["low", "medium", "high"], secondChatModel: "gpt-4.1");
        var repository = AiConnectionRepositoryTests.CreateRepository(db);
        var chat = Chat(profile);
        var other = profile.ConfiguredModels.Single(model => model.RemoteModelId == "gpt-4.1");
        var embedding = new AiWorkspaceModelSelection(profile.Id, Embedding(profile).Id);
        Assert.True(
            (await repository.SelectPurposesAsync(clientId, new(new(profile.Id, chat.Id, "medium"), new(profile.Id, chat.Id, "high"), embedding))).Applied);

        Assert.True((await repository.SelectPurposesAsync(clientId, new(new(profile.Id, chat.Id), new(profile.Id, other.Id), embedding))).Applied);

        var bindings = (await repository.GetByIdAsync(profile.Id))!.PurposeBindings;
        Assert.Equal("medium", bindings.Single(binding => binding.Purpose == AiPurpose.ReviewDefault).ReasoningLevel);
        Assert.Null(bindings.Single(binding => binding.Purpose == AiPurpose.ReviewHighEffort).ReasoningLevel);
    }

    [Fact]
    public async Task SelectPurposesAsync_RemovesTheLevelOfASlotForAnEmptyValue()
    {
        await using var db = CreateContext();
        var (clientId, profile) = await SeedAsync(db, ["low", "medium", "high"]);
        var repository = AiConnectionRepositoryTests.CreateRepository(db);
        var chat = Chat(profile);
        var embedding = new AiWorkspaceModelSelection(profile.Id, Embedding(profile).Id);
        Assert.True(
            (await repository.SelectPurposesAsync(clientId, new(new(profile.Id, chat.Id, "medium"), new(profile.Id, chat.Id, "high"), embedding))).Applied);

        Assert.True(
            (await repository.SelectPurposesAsync(clientId, new(new(profile.Id, chat.Id, string.Empty), new(profile.Id, chat.Id, "HIGH"), embedding))).Applied);

        var bindings = (await repository.GetByIdAsync(profile.Id))!.PurposeBindings;
        Assert.All(DefaultPurposes, purpose => Assert.Null(bindings.Single(binding => binding.Purpose == purpose).ReasoningLevel));
        Assert.Equal("high", bindings.Single(binding => binding.Purpose == AiPurpose.ReviewHighEffort).ReasoningLevel);
    }

    [Theory]
    [InlineData("xhigh", null, "does not accept the reasoning level 'xhigh'")]
    [InlineData("not a level", null, "is not a reasoning level name")]
    [InlineData(null, "low", "The Embedding model takes no reasoning level.")]
    public async Task SelectPurposesAsync_RefusesALevelTheModelDoesNotAcceptAndALevelForEmbedding(string? chatLevel, string? embeddingLevel, string error)
    {
        await using var db = CreateContext();
        var (clientId, profile) = await SeedAsync(db, ["low", "high"]);
        var repository = AiConnectionRepositoryTests.CreateRepository(db);
        var chat = Chat(profile);
        var before = (await repository.GetByIdAsync(profile.Id))!.PurposeBindings;

        var result = await repository.SelectPurposesAsync(
            clientId,
            new(new(profile.Id, chat.Id, chatLevel), new(profile.Id, chat.Id), new(profile.Id, Embedding(profile).Id, embeddingLevel)));

        Assert.False(result.Applied);
        Assert.False(result.Conflict);
        Assert.Contains(error, result.Error, StringComparison.Ordinal);
        Assert.Equal(before, (await repository.GetByIdAsync(profile.Id))!.PurposeBindings);
    }

    // A connection save sends the bindings without their levels: the levels belong to the workspace selection, and a
    // save of the endpoint, the key or a model must not reset them.
    [Fact]
    public async Task UpdateAsync_KeepsTheLevelOfABindingWhileTheBindingKeepsItsModel()
    {
        await using var db = CreateContext();
        var (clientId, profile) = await SeedAsync(db, ["low", "medium", "high"], secondChatModel: "gpt-4.1");
        var repository = AiConnectionRepositoryTests.CreateRepository(db);
        var chat = Chat(profile);
        Assert.True(
            (await repository.SelectPurposesAsync(
                clientId,
                new(new(profile.Id, chat.Id, "medium"), new(profile.Id, chat.Id, "high"), new(profile.Id, Embedding(profile).Id)))).Applied);
        var saved = (await repository.GetByIdAsync(profile.Id))!;
        var other = saved.ConfiguredModels.Single(model => model.RemoteModelId == "gpt-4.1");
        // The save states no level on any binding and moves High effort to the other model.
        var bindings = saved.PurposeBindings
            .Select(binding => binding.Purpose == AiPurpose.ReviewHighEffort
                ? binding with { ConfiguredModelId = other.Id, RemoteModelId = other.RemoteModelId, ReasoningLevel = null }
                : binding with { ReasoningLevel = null })
            .ToList();

        Assert.True(
            await repository.UpdateAsync(
                profile.Id,
                new AiConnectionWriteRequestDto(
                    "Renamed", saved.ProviderKind, saved.BaseUrl, saved.AuthMode, saved.DiscoveryMode, saved.ConfiguredModels, bindings)));

        var stored = (await repository.GetByIdAsync(profile.Id))!.PurposeBindings;
        Assert.All(DefaultPurposes, purpose => Assert.Equal("medium", stored.Single(binding => binding.Purpose == purpose).ReasoningLevel));
        Assert.Null(stored.Single(binding => binding.Purpose == AiPurpose.ReviewHighEffort).ReasoningLevel);
    }

    private static async Task<(Guid ClientId, AiConnectionProfileRecord Profile)> SeedAsync(
        MeisterProPRDbContext db,
        string[] levels,
        string? secondChatModel = null)
    {
        var clientId = Guid.NewGuid();
        db.Clients.Add(new ClientRecord { Id = clientId, TenantId = Guid.NewGuid(), DisplayName = "Owner" });
        var profile = AiConnectionRepositoryTests.MakeProfile(clientId, false, true);
        profile.AuthMode = "ApiKey";
        var chat = Chat(profile);
        chat.ReasoningLevels = levels;
        if (secondChatModel is not null)
        {
            profile.ConfiguredModels.Add(
                new AiConfiguredModelRecord
                {
                    Id = Guid.NewGuid(),
                    ConnectionProfileId = profile.Id,
                    RemoteModelId = secondChatModel,
                    DisplayName = secondChatModel,
                    OperationKinds = chat.OperationKinds,
                    SupportedProtocolModes = chat.SupportedProtocolModes,
                    SupportsStructuredOutput = true,
                    SupportsToolUse = true,
                    Source = chat.Source,
                    ReasoningLevels = levels,
                });
        }

        db.Add(profile);
        await db.SaveChangesAsync();
        return (clientId, profile);
    }

    private static AiConfiguredModelRecord Chat(AiConnectionProfileRecord profile)
    {
        return profile.ConfiguredModels.First(model => model.RemoteModelId == "gpt-4o");
    }

    private static AiConfiguredModelRecord Embedding(AiConnectionProfileRecord profile)
    {
        return profile.ConfiguredModels.Single(model => model.OperationKinds.Contains("Embedding"));
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
