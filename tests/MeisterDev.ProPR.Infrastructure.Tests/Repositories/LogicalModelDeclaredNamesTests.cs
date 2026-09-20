// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Reading and saving a logical model whose wire shape is stored under the names its family declares.
/// </summary>
/// <remarks>
///     A logical model carries no family of its own, so the wire shape on the row is read against the family of
///     the connection the row maps to. A read that used the product's own member names alone reported a migrated
///     row as unresolvable, and a save that wrote the member name back respelled it on the first edit.
/// </remarks>
public sealed class LogicalModelDeclaredNamesTests
{
    private const string DeclaredKey = "meisterdev/googleVertex";
    private const string DeclaredProtocolMode = DeclaredKey + ":GoogleGenerateContent";

    // A wire shape of the family an entry is repointed to, which this family never declares.
    private const string OpenAiKey = "meisterdev/openAi";
    private const string OpenAiChatCompletions = OpenAiKey + ":ChatCompletions";

    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly Guid _connectionId = Guid.NewGuid();
    private readonly Guid _configuredModelId = Guid.NewGuid();

    [Fact]
    public async Task AWireShapeStoredUnderTheFamilysDeclaredNameReadsBackAsThatShape()
    {
        await using var db = this.CreateContext();
        await this.SeedAsync(db, DeclaredKey, DeclaredProtocolMode);

        var entry = Assert.Single(await this.CreateRepository(db).GetTenantEntriesAsync(this._tenantId, default));

        Assert.Equal(DeclaredProtocolMode, entry.ProtocolMode);
        Assert.Null(entry.UnresolvedProtocolMode);
    }

    [Fact]
    public async Task SavingARowStoredUnderTheDeclaredNameLeavesTheStoredSpellingAsItIs()
    {
        await using var db = this.CreateContext();
        await this.SeedAsync(db, DeclaredKey, DeclaredProtocolMode);
        var repository = this.CreateRepository(db);

        var read = Assert.Single(await repository.GetTenantEntriesAsync(this._tenantId, default));
        Assert.True(
            await repository.UpdateTenantEntryAsync(
                this._tenantId,
                read.Name,
                read with { ReasoningEffort = ReviewReasoningEffort.High },
                default));

        var stored = await ReadStoredTenantEntryAsync(db, this._tenantId);
        Assert.Equal(ReviewReasoningEffort.High, stored.ReasoningEffort);
        Assert.Equal(DeclaredProtocolMode, stored.ProtocolMode);
    }

    // The same edit on a row that still holds what the family superseded: the spelling a row holds is the
    // spelling it keeps, whichever of the two it is. Rewriting it here would move the row ahead of the migration
    // that owns that step.
    [Fact]
    public async Task SavingARowStillHoldingTheSupersededNameLeavesItAsItIs()
    {
        await using var db = this.CreateContext();
        await this.SeedAsync(db, "GoogleVertex", "GoogleGenerateContent");
        var repository = this.CreateRepository(db);

        var read = Assert.Single(await repository.GetTenantEntriesAsync(this._tenantId, default));
        Assert.Equal(DeclaredProtocolMode, read.ProtocolMode);

        Assert.True(
            await repository.UpdateTenantEntryAsync(
                this._tenantId,
                read.Name,
                read with { ReasoningEffort = ReviewReasoningEffort.High },
                default));

        var stored = await ReadStoredTenantEntryAsync(db, this._tenantId);
        Assert.Equal("GoogleGenerateContent", stored.ProtocolMode);
    }

    // Nothing is stored to keep, so the family names the shape as it names it now.
    [Fact]
    public async Task CreatingARowStoresTheNameTheFamilyDeclares()
    {
        await using var db = this.CreateContext();
        await this.SeedAsync(db, DeclaredKey, storedProtocolMode: null);

        await this.CreateRepository(db)
            .AddTenantEntryAsync(this._tenantId, this.Entry("deep", DeclaredProtocolMode), default);

        var stored = await ReadStoredTenantEntryAsync(db, this._tenantId);
        Assert.Equal(DeclaredProtocolMode, stored.ProtocolMode);
    }

    // The per-client override is written and read by its own methods, which is where the two near-identical
    // paths could diverge.
    [Fact]
    public async Task CreatingAClientOverrideStoresTheNameTheFamilyDeclares()
    {
        await using var db = this.CreateContext();
        await this.SeedAsync(db, DeclaredKey, storedProtocolMode: null);
        var repository = this.CreateRepository(db);

        await repository.AddClientOverrideAsync(
            this._clientId,
            this.Entry("fast", DeclaredProtocolMode),
            default);

        var stored = await db.LogicalModelOverrides.AsNoTracking().SingleAsync(x => x.ClientId == this._clientId);
        Assert.Equal(DeclaredProtocolMode, stored.ProtocolMode);

        var read = Assert.Single(await repository.GetClientOverridesAsync(this._clientId, default));
        Assert.Equal(DeclaredProtocolMode, read.ProtocolMode);
        Assert.Null(read.UnresolvedProtocolMode);
    }

    // A family qualifies the modes it declared and nothing else. A wire shape it does not serve keeps the
    // product's own member name, because a value qualified by a family that never declared that mode names
    // nothing and would read back as unresolvable.
    [Fact]
    public async Task AWireShapeTheFamilyDoesNotDeclareKeepsTheProductsOwnName()
    {
        await using var db = this.CreateContext();
        await this.SeedAsync(db, DeclaredKey, storedProtocolMode: null);
        var repository = this.CreateRepository(db);

        await repository.AddTenantEntryAsync(
            this._tenantId,
            this.Entry("deep", OpenAiChatCompletions),
            default);

        var stored = await ReadStoredTenantEntryAsync(db, this._tenantId);
        Assert.Equal(OpenAiChatCompletions, stored.ProtocolMode);

        var read = Assert.Single(await repository.GetTenantEntriesAsync(this._tenantId, default));
        Assert.Equal(ProviderDeclaredProtocolModes.Auto, read.ProtocolMode);
        Assert.Equal(OpenAiChatCompletions, read.UnresolvedProtocolMode);
    }

    // A host-reserved wire shape belongs to no family and persists unqualified, which is why it is the one value
    // a row can hold before and after its family declares a vocabulary.
    [Fact]
    public async Task AHostReservedWireShapeStaysUnqualified()
    {
        await using var db = this.CreateContext();
        await this.SeedAsync(db, DeclaredKey, storedProtocolMode: null);

        await this.CreateRepository(db)
            .AddTenantEntryAsync(this._tenantId, this.Entry("deep", ProviderDeclaredProtocolModes.Auto), default);

        var stored = await ReadStoredTenantEntryAsync(db, this._tenantId);
        Assert.Equal(ProviderDeclaredProtocolModes.Auto, stored.ProtocolMode);
    }

    // The connection decides which family's vocabulary the row is read and written against, so repointing the
    // row at a family that never declared the stored shape rewrites it under the names that family uses. The
    // shape is submitted in the unqualified spelling the destination supersedes, because a value submitted
    // already qualified would be stored the same way whether the destination was consulted or not.
    [Fact]
    public async Task RepointingARowAtAnotherFamilyRewritesTheWireShape()
    {
        await using var db = this.CreateContext();
        await this.SeedAsync(db, DeclaredKey, DeclaredProtocolMode);

        var openAiConnectionId = Guid.NewGuid();
        db.AiConnectionProfiles.Add(this.Connection(openAiConnectionId, OpenAiKey));
        await db.SaveChangesAsync();

        var repository = this.CreateRepository(db);
        var read = Assert.Single(await repository.GetTenantEntriesAsync(this._tenantId, default));

        Assert.True(
            await repository.UpdateTenantEntryAsync(
                this._tenantId,
                read.Name,
                read with { ConnectionId = openAiConnectionId, ProtocolMode = "ChatCompletions" },
                default));

        var stored = await ReadStoredTenantEntryAsync(db, this._tenantId);
        Assert.Equal(OpenAiChatCompletions, stored.ProtocolMode);
    }

    private static Task<LogicalModelRecord> ReadStoredTenantEntryAsync(MeisterProPRDbContext db, Guid tenantId)
    {
        return db.LogicalModels.AsNoTracking().SingleAsync(x => x.TenantId == tenantId);
    }

    private LogicalModelDto Entry(string name, string protocolMode)
    {
        return new LogicalModelDto(
            Guid.NewGuid(),
            name,
            AiOperationKind.Chat,
            this._connectionId,
            this._configuredModelId,
            ReviewReasoningEffort.None,
            protocolMode);
    }

    // The connection the logical model maps to, stored under the identity the test is exercising, plus the
    // client row the override path reads its tenant from. A tenant entry is seeded only when the test needs one
    // already on disk.
    private async Task SeedAsync(MeisterProPRDbContext db, string storedIdentity, string? storedProtocolMode)
    {
        var now = DateTimeOffset.UtcNow;

        db.Clients.Add(
            new ClientRecord
            {
                Id = this._clientId,
                TenantId = this._tenantId,
                DisplayName = "Logical Model Test Client",
                IsActive = true,
                CreatedAt = now,
            });
        db.AiConnectionProfiles.Add(this.Connection(this._connectionId, storedIdentity));

        if (storedProtocolMode is not null)
        {
            db.LogicalModels.Add(
                new LogicalModelRecord
                {
                    Id = Guid.NewGuid(),
                    TenantId = this._tenantId,
                    Name = "deep",
                    Capability = AiOperationKind.Chat,
                    ConnectionId = this._connectionId,
                    ConfiguredModelId = this._configuredModelId,
                    ReasoningEffort = ReviewReasoningEffort.None,
                    ProtocolMode = storedProtocolMode,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
        }

        await db.SaveChangesAsync();
    }

    private AiConnectionProfileRecord Connection(Guid connectionId, string storedIdentity)
    {
        var now = DateTimeOffset.UtcNow;

        return new AiConnectionProfileRecord
        {
            Id = connectionId,
            ClientId = this._clientId,
            DisplayName = "Seeded Connection",
            ProviderKind = storedIdentity,
            BaseUrl = "https://europe-west4-aiplatform.googleapis.com",
            AuthMode = "ApiKey",
            DiscoveryMode = nameof(AiDiscoveryMode.ManualOnly),
            DefaultHeaders = [],
            DefaultQueryParams = [],
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    // The declaration the family ships: its own key, the modes it owns, and the unqualified spellings each of
    // those supersedes.
    private static IAiProviderDriverRegistry DeclaringFamily()
    {
        return DeclaringProviderFamilies.DeclaringAll(
            GoogleVertexDeclaration(),
            OpenAiDeclaration());
    }

    // The family a row is repointed at. Registered alongside the first so the repointing test reads the row
    // against this family's vocabulary; with only one family served, the destination resolves as unknown and the
    // read falls back to the value as stored, which would hold whatever the destination declared.
    private static ProviderDeclaration OpenAiDeclaration()
    {
        return new ProviderDeclaration
        {
            Key = OpenAiKey,
            Label = "OpenAI",
            Version = "1.0",
            ContractVersion = ProviderContract.Version,
            LegacyNames = ProviderLegacyNames.FromUnqualifiedNames(
                ["OpenAi"],
                [OpenAiKey + ":ApiKey"],
                [
                    ProviderDeclaredProtocolModes.Auto,
                    ProviderVocabulary.Compose(OpenAiKey, "Responses"),
                    ProviderVocabulary.Compose(OpenAiKey, "ChatCompletions"),
                    ProviderDeclaredProtocolModes.Embeddings,
                ]),
            AuthModes = [new ProviderDeclaredAuthMode(OpenAiKey + ":ApiKey", [AiCredentialFieldSupport.ApiKey])],
            ProtocolModes = new ProviderDeclaredProtocolModes(
            [
                ProviderDeclaredProtocolModes.Auto,
                ProviderVocabulary.Compose(OpenAiKey, "Responses"),
                ProviderVocabulary.Compose(OpenAiKey, "ChatCompletions"),
                ProviderDeclaredProtocolModes.Embeddings,
            ]),
            ConformanceInputs = new ProviderConformanceInputs(OpenAiKey + ":ApiKey"),
        };
    }

    private static ProviderDeclaration GoogleVertexDeclaration()
    {
        return new ProviderDeclaration
        {
            Key = DeclaredKey,
            Label = "Google Vertex AI",
            Version = "1.0",
            ContractVersion = ProviderContract.Version,
            LegacyNames = ProviderLegacyNames.FromUnqualifiedNames(
                ["GoogleVertex"],
                [DeclaredKey + ":ApiKey", DeclaredKey + ":GcpAdc"],
                [DeclaredProtocolMode]),
            AuthModes =
            [
                new ProviderDeclaredAuthMode(DeclaredKey + ":ApiKey", [AiCredentialFieldSupport.ApiKey]),
                new ProviderDeclaredAuthMode(DeclaredKey + ":GcpAdc", [AiCredentialFieldSupport.ApiKey]),
            ],
            ProtocolModes = new ProviderDeclaredProtocolModes(
            [
                ProviderDeclaredProtocolModes.Auto,
                DeclaredProtocolMode,
                ProviderDeclaredProtocolModes.Embeddings,
            ]),
            ConformanceInputs = new ProviderConformanceInputs(DeclaredKey + ":ApiKey"),
        };
    }

    // Capability validation and connection scoping have their own coverage; these tests exercise the vocabulary
    // the row carries, so both are permissive here.
    private LogicalModelCatalogRepository CreateRepository(MeisterProPRDbContext db)
    {
        return new LogicalModelCatalogRepository(
            db,
            Substitute.For<ILogicalModelCapabilityValidator>(),
            Substitute.For<IAiConnectionRepository>(),
            Substitute.For<IAiConnectionScopeGuard>(),
            DeclaringFamily());
    }

    private MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase(this._tenantId.ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new MeisterProPRDbContext(options);
    }
}
