// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Exceptions;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Integration tests for <see cref="LogicalModelCatalogRepository" /> against a real PostgreSQL instance. Covers the
///     tenant-catalog + per-client-override scoping, name uniqueness within a scope, and the system-tenant rule.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class LogicalModelCatalogRepositoryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _otherTenantId = Guid.NewGuid();
    private readonly Guid _clientA = Guid.NewGuid();
    private readonly Guid _clientB = Guid.NewGuid();
    private readonly Guid _otherClient = Guid.NewGuid();
    private readonly Guid _systemClient = Guid.NewGuid();
    private MeisterProPRDbContext _dbContext = null!;
    private ILogicalModelCatalogRepository _repo = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._dbContext = new MeisterProPRDbContext(options);

        var now = DateTimeOffset.UtcNow;

        // The system tenant is a shared fixture row seeded once for the whole container; add it only if absent.
        if (!await this._dbContext.Tenants.AnyAsync(t => t.Id == TenantCatalog.SystemTenantId))
        {
            this._dbContext.Tenants.Add(
                new TenantRecord
                {
                    Id = TenantCatalog.SystemTenantId,
                    Slug = TenantCatalog.SystemTenantSlug,
                    DisplayName = TenantCatalog.SystemTenantDisplayName,
                    IsActive = true,
                    LocalLoginEnabled = false,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
        }

        this._dbContext.Tenants.Add(NewTenant(this._tenantId, now));
        this._dbContext.Tenants.Add(NewTenant(this._otherTenantId, now));
        this._dbContext.Clients.Add(NewClient(this._clientA, this._tenantId, now));
        this._dbContext.Clients.Add(NewClient(this._clientB, this._tenantId, now));
        this._dbContext.Clients.Add(NewClient(this._otherClient, this._otherTenantId, now));
        this._dbContext.Clients.Add(NewClient(this._systemClient, TenantCatalog.SystemTenantId, now));
        await this._dbContext.SaveChangesAsync();

        // These tests exercise persistence + scoping only; capability validation has its own coverage,
        // so a permissive validator is substituted here.
        this._repo = new LogicalModelCatalogRepository(
            this._dbContext,
            Substitute.For<ILogicalModelCapabilityValidator>());
    }

    public async Task DisposeAsync()
    {
        if (this._dbContext is null)
        {
            return;
        }

        // Clean up only the rows this test seeded. The shared system tenant row is left in place.
        Guid[] myClients = [this._clientA, this._clientB, this._otherClient, this._systemClient];
        Guid[] myTenants = [this._tenantId, this._otherTenantId];

        await this._dbContext.LogicalModelOverrides.Where(x => myClients.Contains(x.ClientId)).ExecuteDeleteAsync();
        await this._dbContext.LogicalModels.Where(x => myTenants.Contains(x.TenantId)).ExecuteDeleteAsync();
        await this._dbContext.Clients.Where(c => myClients.Contains(c.Id)).ExecuteDeleteAsync();
        await this._dbContext.Tenants.Where(t => myTenants.Contains(t.Id)).ExecuteDeleteAsync();
        await this._dbContext.DisposeAsync();
    }

    // AC #1: a logical model created at tenant scope is readable for any client in that tenant.
    [Fact]
    public async Task TenantEntry_IsReadable_ForEveryClientInTheTenant()
    {
        await this._repo.AddTenantEntryAsync(this._tenantId, Entry("deep"), default);

        var forA = await this._repo.GetTenantEntriesForClientAsync(this._clientA, default);
        var forB = await this._repo.GetTenantEntriesForClientAsync(this._clientB, default);

        Assert.Contains(forA, m => m.Name == "deep");
        Assert.Contains(forB, m => m.Name == "deep");
    }

    // AC #2: a client override is stored and read back for that client only.
    [Fact]
    public async Task ClientOverride_IsReadBack_ForThatClientOnly()
    {
        await this._repo.AddClientOverrideAsync(this._clientA, Entry("fast"), default);

        var forA = await this._repo.GetClientOverridesAsync(this._clientA, default);
        var forB = await this._repo.GetClientOverridesAsync(this._clientB, default);

        Assert.Contains(forA, m => m.Name == "fast");
        Assert.DoesNotContain(forB, m => m.Name == "fast");
    }

    // AC #2 (round-trip): the stored override reads back with the same mapping + settings. Uses an EMBEDDING model
    // with non-zero enum values across all three converted columns so a broken HasConversion<int>() cannot hide behind
    // the zero-valued Chat/None/Auto defaults.
    [Fact]
    public async Task ClientOverride_RoundTripsAllFields()
    {
        var connectionId = Guid.NewGuid();
        var modelId = Guid.NewGuid();
        var entry = new LogicalModelDto(
            Guid.NewGuid(),
            "embed",
            AiOperationKind.Embedding,
            connectionId,
            modelId,
            ReviewReasoningEffort.High,
            AiProtocolMode.Embeddings);

        await this._repo.AddClientOverrideAsync(this._clientA, entry, default);

        var read = Assert.Single(await this._repo.GetClientOverridesAsync(this._clientA, default));
        Assert.Equal(entry.Id, read.Id);
        Assert.Equal("embed", read.Name);
        Assert.Equal(AiOperationKind.Embedding, read.Capability);
        Assert.True(read.IsEmbedding);
        Assert.Equal(connectionId, read.ConnectionId);
        Assert.Equal(modelId, read.ConfiguredModelId);
        Assert.Equal(ReviewReasoningEffort.High, read.ReasoningEffort);
        Assert.Equal(AiProtocolMode.Embeddings, read.ProtocolMode);
    }

    // AC #3: a duplicate name within the same tenant is rejected.
    [Fact]
    public async Task DuplicateName_InSameTenant_IsRejected()
    {
        await this._repo.AddTenantEntryAsync(this._tenantId, Entry("deep"), default);

        await Assert.ThrowsAsync<DuplicateLogicalModelException>(() => this._repo.AddTenantEntryAsync(this._tenantId, Entry("deep"), default));
    }

    // AC #3: a duplicate name for the same client is rejected.
    [Fact]
    public async Task DuplicateName_ForSameClient_IsRejected()
    {
        await this._repo.AddClientOverrideAsync(this._clientA, Entry("deep"), default);

        await Assert.ThrowsAsync<DuplicateLogicalModelException>(() => this._repo.AddClientOverrideAsync(this._clientA, Entry("deep"), default));
    }

    // AC #3 (note): a tenant entry and a client override may share a name — that is how shadowing works.
    [Fact]
    public async Task TenantEntry_AndClientOverride_MayShareAName()
    {
        await this._repo.AddTenantEntryAsync(this._tenantId, Entry("deep"), default);

        // Must not throw: the override shadows the tenant entry of the same name.
        await this._repo.AddClientOverrideAsync(this._clientA, Entry("deep"), default);

        Assert.Contains(await this._repo.GetTenantEntriesAsync(this._tenantId, default), m => m.Name == "deep");
        Assert.Contains(await this._repo.GetClientOverridesAsync(this._clientA, default), m => m.Name == "deep");
    }

    // AC #3 (scope isolation): the same name is allowed in two different tenants.
    [Fact]
    public async Task SameName_InDifferentTenants_IsAllowed()
    {
        await this._repo.AddTenantEntryAsync(this._tenantId, Entry("deep"), default);
        await this._repo.AddTenantEntryAsync(this._otherTenantId, Entry("deep"), default);

        Assert.Contains(await this._repo.GetTenantEntriesAsync(this._tenantId, default), m => m.Name == "deep");
        Assert.Contains(await this._repo.GetTenantEntriesAsync(this._otherTenantId, default), m => m.Name == "deep");
    }

    // AC #1/#3 (isolation): tenant reads are filtered by tenant — one tenant (and its clients) never sees another
    // tenant's entries. Uses DISTINCT names per tenant so a filter that returned all rows would fail here.
    [Fact]
    public async Task TenantEntries_AreIsolatedByTenant()
    {
        await this._repo.AddTenantEntryAsync(this._tenantId, Entry("deep"), default);
        await this._repo.AddTenantEntryAsync(this._otherTenantId, Entry("wide"), default);

        var forTenant = await this._repo.GetTenantEntriesAsync(this._tenantId, default);
        Assert.Contains(forTenant, m => m.Name == "deep");
        Assert.DoesNotContain(forTenant, m => m.Name == "wide");

        // Resolved through a client of the other tenant: it sees its own tenant's entry, not the first tenant's.
        var forOtherClient = await this._repo.GetTenantEntriesForClientAsync(this._otherClient, default);
        Assert.Contains(forOtherClient, m => m.Name == "wide");
        Assert.DoesNotContain(forOtherClient, m => m.Name == "deep");
    }

    // AC #4: on the system tenant, a tenant-catalog entry cannot be created.
    [Fact]
    public async Task SystemTenant_TenantEntry_IsRejected()
    {
        await Assert.ThrowsAsync<SystemTenantLogicalModelCatalogException>(() => this._repo.AddTenantEntryAsync(
            TenantCatalog.SystemTenantId, Entry("deep"), default));
    }

    // AC #4: the unassigned/empty tenant (which normalizes to the system tenant) is rejected too.
    [Fact]
    public async Task EmptyTenant_TenantEntry_IsRejected()
    {
        await Assert.ThrowsAsync<SystemTenantLogicalModelCatalogException>(() => this._repo.AddTenantEntryAsync(Guid.Empty, Entry("deep"), default));
    }

    // AC #4: on the system tenant, a per-client override can still be created.
    [Fact]
    public async Task SystemTenant_ClientOverride_IsAllowed()
    {
        await this._repo.AddClientOverrideAsync(this._systemClient, Entry("deep"), default);

        Assert.Contains(await this._repo.GetClientOverridesAsync(this._systemClient, default), m => m.Name == "deep");
        // A system-tenant client sees no tenant-catalog entries (the system tenant has none).
        Assert.Empty(await this._repo.GetTenantEntriesForClientAsync(this._systemClient, default));
    }

    // The repository runs capability validation before persisting, so a validator rejection blocks the
    // write and nothing is stored.
    [Fact]
    public async Task AddTenantEntry_WhenValidatorRejects_DoesNotPersist()
    {
        var rejectingValidator = Substitute.For<ILogicalModelCapabilityValidator>();
        rejectingValidator.ValidateAsync(Arg.Any<LogicalModelDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new LogicalModelReferenceInvalidException("deep", "model does not support chat")));
        var repo = new LogicalModelCatalogRepository(this._dbContext, rejectingValidator);

        await Assert.ThrowsAsync<LogicalModelReferenceInvalidException>(() => repo.AddTenantEntryAsync(this._tenantId, Entry("deep"), default));

        Assert.Empty(await this._repo.GetTenantEntriesAsync(this._tenantId, default));
    }

    // The client-override path must validate too (guards against a copy-paste divergence between the two
    // near-identical write methods).
    [Fact]
    public async Task AddClientOverride_WhenValidatorRejects_DoesNotPersist()
    {
        var rejectingValidator = Substitute.For<ILogicalModelCapabilityValidator>();
        rejectingValidator.ValidateAsync(Arg.Any<LogicalModelDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new LogicalModelReferenceInvalidException("fast", "model does not support chat")));
        var repo = new LogicalModelCatalogRepository(this._dbContext, rejectingValidator);

        await Assert.ThrowsAsync<LogicalModelReferenceInvalidException>(() => repo.AddClientOverrideAsync(this._clientA, Entry("fast"), default));

        Assert.Empty(await this._repo.GetClientOverridesAsync(this._clientA, default));
    }

    // Delete removes the entry and reports found/not-found.
    [Fact]
    public async Task DeleteTenantEntry_RemovesIt_AndReportsNotFound()
    {
        await this._repo.AddTenantEntryAsync(this._tenantId, Entry("deep"), default);

        Assert.True(await this._repo.DeleteTenantEntryAsync(this._tenantId, "deep", default));
        Assert.DoesNotContain(await this._repo.GetTenantEntriesAsync(this._tenantId, default), m => m.Name == "deep");
        Assert.False(await this._repo.DeleteTenantEntryAsync(this._tenantId, "deep", default));
    }

    [Fact]
    public async Task DeleteClientOverride_RemovesIt_ForThatClientOnly()
    {
        await this._repo.AddClientOverrideAsync(this._clientA, Entry("fast"), default);

        Assert.True(await this._repo.DeleteClientOverrideAsync(this._clientA, "fast", default));
        Assert.Empty(await this._repo.GetClientOverridesAsync(this._clientA, default));
    }

    // Rename changes the business key and enforces uniqueness of the new name in scope.
    [Fact]
    public async Task RenameTenantEntry_ChangesName()
    {
        await this._repo.AddTenantEntryAsync(this._tenantId, Entry("deep"), default);

        Assert.True(await this._repo.RenameTenantEntryAsync(this._tenantId, "deep", "deep-turbo", default));

        var entries = await this._repo.GetTenantEntriesAsync(this._tenantId, default);
        Assert.Contains(entries, m => m.Name == "deep-turbo");
        Assert.DoesNotContain(entries, m => m.Name == "deep");
    }

    [Fact]
    public async Task RenameTenantEntry_ToExistingName_IsRejected()
    {
        await this._repo.AddTenantEntryAsync(this._tenantId, Entry("deep"), default);
        await this._repo.AddTenantEntryAsync(this._tenantId, Entry("fast"), default);

        await Assert.ThrowsAsync<DuplicateLogicalModelException>(() => this._repo.RenameTenantEntryAsync(this._tenantId, "deep", "fast", default));
    }

    [Fact]
    public async Task RenameClientOverride_ChangesName_AndReportsNotFound()
    {
        await this._repo.AddClientOverrideAsync(this._clientA, Entry("fast"), default);

        Assert.True(await this._repo.RenameClientOverrideAsync(this._clientA, "fast", "fast-2", default));
        Assert.Contains(await this._repo.GetClientOverridesAsync(this._clientA, default), m => m.Name == "fast-2");
        Assert.False(await this._repo.RenameClientOverrideAsync(this._clientA, "missing", "x", default));
    }

    // Per-client purpose → logical-model-name map round-trips (set/upsert/get/get-all/remove), scoped
    // per client + purpose.
    [Fact]
    public async Task PurposeRole_SetUpsertGetRemove_RoundTrips()
    {
        Assert.Null(await this._repo.GetPurposeRoleAsync(this._clientA, AiPurpose.ReviewTriage, default));

        await this._repo.SetPurposeRoleAsync(this._clientA, AiPurpose.ReviewTriage, "triage-role", default);
        Assert.Equal("triage-role", await this._repo.GetPurposeRoleAsync(this._clientA, AiPurpose.ReviewTriage, default));

        // Upsert replaces the existing mapping for the same client + purpose.
        await this._repo.SetPurposeRoleAsync(this._clientA, AiPurpose.ReviewTriage, "triage-role-2", default);
        Assert.Equal("triage-role-2", await this._repo.GetPurposeRoleAsync(this._clientA, AiPurpose.ReviewTriage, default));

        // Scoped to this client only.
        Assert.Null(await this._repo.GetPurposeRoleAsync(this._clientB, AiPurpose.ReviewTriage, default));

        await this._repo.SetPurposeRoleAsync(this._clientA, AiPurpose.EmbeddingDefault, "embed-role", default);
        var all = await this._repo.GetPurposeRolesAsync(this._clientA, default);
        Assert.Equal("triage-role-2", all[AiPurpose.ReviewTriage]);
        Assert.Equal("embed-role", all[AiPurpose.EmbeddingDefault]);

        Assert.True(await this._repo.RemovePurposeRoleAsync(this._clientA, AiPurpose.ReviewTriage, default));
        Assert.Null(await this._repo.GetPurposeRoleAsync(this._clientA, AiPurpose.ReviewTriage, default));
        Assert.False(await this._repo.RemovePurposeRoleAsync(this._clientA, AiPurpose.ReviewTriage, default));
    }

    private static LogicalModelDto Entry(string name)
    {
        return new LogicalModelDto(
            Guid.NewGuid(),
            name,
            AiOperationKind.Chat,
            Guid.NewGuid(),
            Guid.NewGuid(),
            ReviewReasoningEffort.None,
            AiProtocolMode.Auto);
    }

    private static TenantRecord NewTenant(Guid id, DateTimeOffset now)
    {
        return new TenantRecord
        {
            Id = id,
            Slug = "lm-" + id.ToString("N"),
            DisplayName = "Logical Model Test Tenant",
            IsActive = true,
            LocalLoginEnabled = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static ClientRecord NewClient(Guid id, Guid tenantId, DateTimeOffset now)
    {
        return new ClientRecord
        {
            Id = id,
            TenantId = tenantId,
            DisplayName = "Logical Model Test Client",
            IsActive = true,
            CreatedAt = now,
        };
    }
}
