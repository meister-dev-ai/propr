// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using MeisterProPR.Infrastructure.Data.Models;
using MeisterProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterProPR.Infrastructure.Repositories;
using MeisterProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterProPR.Infrastructure.Tests.Repositories.ProCursor;

/// <summary>
///     Integration tests for the ProCursor repository layer against PostgreSQL + pgvector.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class ProCursorRepositoryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private static readonly Guid ClientId = Guid.Parse("cccccccc-1000-0000-0000-000000000001");

    private MeisterProPRDbContext _db = null!;
    private ProCursorIndexJobRepository _jobs = null!;
    private ProCursorKnowledgeSourceRepository _knowledgeSources = null!;
    private ProCursorIndexSnapshotRepository _snapshots = null!;
    private ProCursorSymbolGraphRepository _symbolGraph = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._db = new MeisterProPRDbContext(options);

        if (!await this._db.Clients.AnyAsync(client => client.Id == ClientId))
        {
            this._db.Clients.Add(
                new ClientRecord
                {
                    Id = ClientId,
                    TenantId = TenantCatalog.SystemTenantId,
                    DisplayName = "ProCursor Test Client",
                    IsActive = true,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            await this._db.SaveChangesAsync();
        }

        await this._db.ProCursorSymbolEdges.ExecuteDeleteAsync();
        await this._db.ProCursorSymbolRecords.ExecuteDeleteAsync();
        await this._db.ProCursorKnowledgeChunks.ExecuteDeleteAsync();
        await this._db.ProCursorIndexSnapshots.ExecuteDeleteAsync();
        await this._db.ProCursorIndexJobs.ExecuteDeleteAsync();
        await this._db.ProCursorTrackedBranches.ExecuteDeleteAsync();
        await this._db.ProCursorKnowledgeSources.ExecuteDeleteAsync();

        this._knowledgeSources = new ProCursorKnowledgeSourceRepository(this._db);
        this._jobs = new ProCursorIndexJobRepository(this._db);
        this._snapshots = new ProCursorIndexSnapshotRepository(this._db);
        this._symbolGraph = new ProCursorSymbolGraphRepository(this._db);
    }

    public async Task DisposeAsync()
    {
        if (this._db is not null)
        {
            await this._db.DisposeAsync();
        }
    }

    [Fact]
    public async Task KnowledgeSourceRepository_AddAsync_PersistsSourceAndTrackedBranch()
    {
        var source = CreateSource();
        source.AddTrackedBranch(Guid.NewGuid(), "main", ProCursorRefreshTriggerMode.BranchUpdate, true);

        await this._knowledgeSources.AddAsync(source);

        var stored = await this._knowledgeSources.GetByIdAsync(ClientId, source.Id);
        Assert.NotNull(stored);
        Assert.Single(stored.TrackedBranches);
    }

    [Fact]
    public async Task IndexJobRepository_GetNextPendingAsync_ReturnsOldestPendingJob()
    {
        var source = await this.PersistSourceWithBranchAsync();
        var branch = source.TrackedBranches.Single();
        var first = new ProCursorIndexJob(Guid.NewGuid(), source.Id, branch.Id, null, "refresh", "source/main/1");
        var second = new ProCursorIndexJob(Guid.NewGuid(), source.Id, branch.Id, null, "refresh", "source/main/2");

        await this._jobs.AddAsync(first);
        await this._jobs.AddAsync(second);

        var next = await this._jobs.GetNextPendingAsync();

        Assert.NotNull(next);
        Assert.Equal(first.Id, next.Id);
    }

    [Fact]
    public async Task IndexJobRepository_HasActiveJobAsync_IgnoresCompletedJobs()
    {
        var source = await this.PersistSourceWithBranchAsync();
        var branch = source.TrackedBranches.Single();
        var job = new ProCursorIndexJob(Guid.NewGuid(), source.Id, branch.Id, null, "refresh", "source/main/active");

        await this._jobs.AddAsync(job);
        Assert.True(await this._jobs.HasActiveJobAsync(branch.Id, job.DedupKey));

        job.MarkProcessing();
        job.MarkCompleted();
        await this._jobs.UpdateAsync(job);

        Assert.False(await this._jobs.HasActiveJobAsync(branch.Id, job.DedupKey));
    }

    [Fact]
    public async Task IndexSnapshotRepository_ReplaceKnowledgeChunksAsync_ReplacesPersistedChunkSet()
    {
        var snapshot = await this.PersistReadySnapshotAsync();

        await this._snapshots.ReplaceKnowledgeChunksAsync(
            snapshot.Id,
            [
                CreateChunk(snapshot.Id, 0, "docs/first.md", V(1f, 0f)),
            ]);
        await this._snapshots.ReplaceKnowledgeChunksAsync(
            snapshot.Id,
            [
                CreateChunk(snapshot.Id, 0, "docs/second.md", V(0f, 1f)),
            ]);

        var chunks = await this._db.ProCursorKnowledgeChunks
            .Where(chunk => chunk.SnapshotId == snapshot.Id)
            .ToListAsync();

        Assert.Single(chunks);
        Assert.Equal("docs/second.md", chunks[0].SourcePath);
    }

    [Fact]
    public async Task ProCursorKnowledgeChunks_SupportCosineOrderingAgainstPersistedVectors()
    {
        var snapshot = await this.PersistReadySnapshotAsync();
        await this._snapshots.ReplaceKnowledgeChunksAsync(
            snapshot.Id,
            [
                CreateChunk(snapshot.Id, 0, "docs/near.md", V(1f, 0f, 0f)),
                CreateChunk(snapshot.Id, 1, "docs/far.md", V(0f, 0f, 1f)),
            ]);

        var queryVector = new Vector(V(0.99f, 0.01f, 0f));

        var nearest = await this._db.ProCursorKnowledgeChunks
            .OrderBy(chunk => chunk.EmbeddingVector.CosineDistance(queryVector))
            .FirstAsync();

        Assert.Equal("docs/near.md", nearest.SourcePath);
    }

    [Fact]
    public async Task IndexSnapshotRepository_GetLatestReadyAsync_ReturnsNewestReadySnapshot()
    {
        var source = await this.PersistSourceWithBranchAsync();
        var branch = source.TrackedBranches.Single();

        var older = new ProCursorIndexSnapshot(Guid.NewGuid(), source.Id, branch.Id, "commit-a", "full");
        older.MarkReady(1, 1, 0, false);
        await this._snapshots.AddAsync(older);

        var newer = new ProCursorIndexSnapshot(Guid.NewGuid(), source.Id, branch.Id, "commit-b", "full");
        newer.MarkReady(2, 2, 0, false);
        await this._snapshots.AddAsync(newer);

        var latest = await this._snapshots.GetLatestReadyAsync(source.Id, branch.Id);

        Assert.NotNull(latest);
        Assert.Equal(newer.Id, latest.Id);
    }

    [Fact]
    public async Task SymbolGraphRepository_ReplaceAsync_PersistsSymbolsAndEdges()
    {
        var snapshot = await this.PersistReadySnapshotAsync();
        var symbol = new ProCursorSymbolRecord(
            Guid.NewGuid(),
            snapshot.Id,
            "csharp",
            "M:Demo.Widget.Run",
            "Run",
            "method",
            null,
            "src/Widget.cs",
            10,
            12,
            "void Demo.Widget.Run()",
            "Demo.Widget.Run");
        var edge = new ProCursorSymbolEdge(
            Guid.NewGuid(),
            snapshot.Id,
            symbol.SymbolKey,
            "M:Demo.Widget.Helper",
            "calls",
            "src/Widget.cs",
            11,
            11);

        await this._symbolGraph.ReplaceAsync(snapshot.Id, [symbol], [edge]);

        Assert.Equal(1, await this._db.ProCursorSymbolRecords.CountAsync(record => record.SnapshotId == snapshot.Id));
        Assert.Equal(1, await this._db.ProCursorSymbolEdges.CountAsync(record => record.SnapshotId == snapshot.Id));
    }

    private async Task<ProCursorKnowledgeSource> PersistSourceWithBranchAsync()
    {
        var source = CreateSource();
        source.AddTrackedBranch(Guid.NewGuid(), "main", ProCursorRefreshTriggerMode.BranchUpdate, true);
        await this._knowledgeSources.AddAsync(source);
        return source;
    }

    private async Task<ProCursorIndexSnapshot> PersistReadySnapshotAsync()
    {
        var source = await this.PersistSourceWithBranchAsync();
        var branch = source.TrackedBranches.Single();
        var snapshot = new ProCursorIndexSnapshot(
            Guid.NewGuid(),
            source.Id,
            branch.Id,
            Guid.NewGuid().ToString("N"),
            "full");
        snapshot.MarkReady(1, 0, 0, false);
        await this._snapshots.AddAsync(snapshot);
        return snapshot;
    }

    private static ProCursorKnowledgeSource CreateSource()
    {
        return new ProCursorKnowledgeSource(
            Guid.NewGuid(),
            ClientId,
            "Repo Source",
            ProCursorSourceKind.Repository,
            "https://dev.azure.com/test-org",
            "test-project",
            Guid.NewGuid().ToString("N"),
            "main",
            null,
            true,
            "auto");
    }

    private static ProCursorKnowledgeChunk CreateChunk(Guid snapshotId, int ordinal, string path, float[] vector)
    {
        return new ProCursorKnowledgeChunk(
            Guid.NewGuid(),
            snapshotId,
            path,
            "wiki_page",
            null,
            ordinal,
            null,
            null,
            Guid.NewGuid().ToString("N"),
            $"chunk for {path}",
            vector);
    }

    private static float[] V(params float[] seed)
    {
        var vector = new float[1536];
        for (var i = 0; i < seed.Length && i < vector.Length; i++)
        {
            vector[i] = seed[i];
        }

        return vector;
    }
}
