using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Tests.Repositories;

/// <summary>Unit tests for <see cref="AiConnectionRepository"/> using EF Core in-memory database.</summary>
public sealed class AiConnectionRepositoryTests
{
    private static MeisterProPRDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new MeisterProPRDbContext(options);
    }

    private static AiConnectionRecord MakeRecord(Guid clientId, bool isActive = false, string? activeModel = null)
    {
        return new AiConnectionRecord
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            DisplayName = "Test Connection",
            EndpointUrl = "https://my-openai.openai.azure.com/",
            Models = ["gpt-4o", "gpt-4o-mini"],
            IsActive = isActive,
            ActiveModel = activeModel,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    [Fact]
    public async Task GetActiveForClientAsync_NoActiveConnection_ReturnsNull()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        db.AiConnections.Add(MakeRecord(clientId, isActive: false));
        await db.SaveChangesAsync();

        var repo = new AiConnectionRepository(db);
        var result = await repo.GetActiveForClientAsync(clientId);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetActiveForClientAsync_OneActiveConnection_ReturnsIt()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var activeRecord = MakeRecord(clientId, isActive: true, activeModel: "gpt-4o");
        db.AiConnections.Add(activeRecord);
        db.AiConnections.Add(MakeRecord(clientId, isActive: false));
        await db.SaveChangesAsync();

        var repo = new AiConnectionRepository(db);
        var result = await repo.GetActiveForClientAsync(clientId);

        Assert.NotNull(result);
        Assert.Equal(activeRecord.Id, result.Id);
        Assert.True(result.IsActive);
        Assert.Equal("gpt-4o", result.ActiveModel);
    }

    [Fact]
    public async Task ActivateAsync_ValidModel_ActivatesConnectionAndDeactivatesPrevious()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();

        // Start with connection A active
        var connA = MakeRecord(clientId, isActive: true, activeModel: "gpt-4o-mini");
        var connB = MakeRecord(clientId, isActive: false);
        connB.Models = ["gpt-4o"];
        db.AiConnections.AddRange(connA, connB);
        await db.SaveChangesAsync();

        var repo = new AiConnectionRepository(db);
        var result = await repo.ActivateAsync(connB.Id, "gpt-4o");

        Assert.True(result);

        // Reload from DB to verify state
        var refreshedA = await db.AiConnections.FindAsync(connA.Id);
        var refreshedB = await db.AiConnections.FindAsync(connB.Id);

        Assert.NotNull(refreshedA);
        Assert.False(refreshedA.IsActive);
        Assert.Null(refreshedA.ActiveModel);

        Assert.NotNull(refreshedB);
        Assert.True(refreshedB.IsActive);
        Assert.Equal("gpt-4o", refreshedB.ActiveModel);
    }

    [Fact]
    public async Task ActivateAsync_ModelNotInList_ReturnsFalse()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var conn = MakeRecord(clientId);
        conn.Models = ["gpt-4o"];
        db.AiConnections.Add(conn);
        await db.SaveChangesAsync();

        var repo = new AiConnectionRepository(db);
        var result = await repo.ActivateAsync(conn.Id, "gpt-5");

        Assert.False(result);
    }

    [Fact]
    public async Task ActivateAsync_ConnectionNotFound_ReturnsFalse()
    {
        await using var db = CreateContext();
        var repo = new AiConnectionRepository(db);

        var result = await repo.ActivateAsync(Guid.NewGuid(), "gpt-4o");

        Assert.False(result);
    }

    [Fact]
    public async Task DeactivateAsync_ActiveConnection_SetsInactive()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var conn = MakeRecord(clientId, isActive: true, activeModel: "gpt-4o");
        db.AiConnections.Add(conn);
        await db.SaveChangesAsync();

        var repo = new AiConnectionRepository(db);
        var result = await repo.DeactivateAsync(conn.Id);

        Assert.True(result);

        var refreshed = await db.AiConnections.FindAsync(conn.Id);
        Assert.NotNull(refreshed);
        Assert.False(refreshed.IsActive);
        Assert.Null(refreshed.ActiveModel);
    }

    [Fact]
    public async Task DeleteAsync_ExistingConnection_Removes()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var conn = MakeRecord(clientId);
        db.AiConnections.Add(conn);
        await db.SaveChangesAsync();

        var repo = new AiConnectionRepository(db);
        var result = await repo.DeleteAsync(conn.Id);

        Assert.True(result);
        Assert.Equal(0, await db.AiConnections.CountAsync());
    }

    [Fact]
    public async Task DeleteAsync_ConnectionNotFound_ReturnsFalse()
    {
        await using var db = CreateContext();
        var repo = new AiConnectionRepository(db);

        var result = await repo.DeleteAsync(Guid.NewGuid());

        Assert.False(result);
    }

    [Fact]
    public async Task ActivateAsync_DoesNotOverwriteJobSnapshot_SC003()
    {
        // SC-003: After switching the active connection for a client, a ReviewJob that previously
        // snapshotted AiConnectionId and AiModel retains its original values.
        // (Jobs hold their snapshot at creation time — ActivateAsync must not touch ReviewJob records.)
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();

        var connA = MakeRecord(clientId, isActive: true, activeModel: "gpt-4o");
        var connB = MakeRecord(clientId, isActive: false);
        connB.Models = ["gpt-4o-mini"];
        db.AiConnections.AddRange(connA, connB);

        // Simulate a job that was created with connA as its AI connection snapshot
        var job = new MeisterProPR.Domain.Entities.ReviewJob(
            Guid.NewGuid(),
            clientId,
            "https://dev.azure.com/org",
            "proj",
            "repo",
            1,
            1);
        job.SetAiConfig(connA.Id, "gpt-4o");
        db.ReviewJobs.Add(job);
        await db.SaveChangesAsync();

        // Now activate connB (switching away from connA)
        var repo = new AiConnectionRepository(db);
        await repo.ActivateAsync(connB.Id, "gpt-4o-mini");

        // The job's snapshot must remain unchanged
        var jobAfter = await db.ReviewJobs.FindAsync(job.Id);
        Assert.NotNull(jobAfter);
        Assert.Equal(connA.Id, jobAfter.AiConnectionId);
        Assert.Equal("gpt-4o", jobAfter.AiModel);
    }

    // ─── T041: GetForTierAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetForTierAsync_TierConnectionExists_ReturnsIt()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        var tierRecord = MakeRecord(clientId);
        tierRecord.ModelCategory = (short)AiConnectionModelCategory.HighEffort;
        db.AiConnections.Add(tierRecord);
        db.AiConnections.Add(MakeRecord(clientId)); // untagged connection
        await db.SaveChangesAsync();

        var repo = new AiConnectionRepository(db);
        var result = await repo.GetForTierAsync(clientId, AiConnectionModelCategory.HighEffort);

        Assert.NotNull(result);
        Assert.Equal(tierRecord.Id, result.Id);
    }

    [Fact]
    public async Task GetForTierAsync_NoTierConnection_ReturnsNull()
    {
        await using var db = CreateContext();
        var clientId = Guid.NewGuid();
        db.AiConnections.Add(MakeRecord(clientId)); // no model category tag
        await db.SaveChangesAsync();

        var repo = new AiConnectionRepository(db);
        var result = await repo.GetForTierAsync(clientId, AiConnectionModelCategory.LowEffort);

        Assert.Null(result);
    }
}
