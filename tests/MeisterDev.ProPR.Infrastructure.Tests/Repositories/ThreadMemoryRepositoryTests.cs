// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.Infrastructure.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using FactAttribute = Xunit.SkippableFactAttribute;
using TheoryAttribute = Xunit.SkippableTheoryAttribute;
using MeisterDev.ProPR.TestSupport;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Integration tests for <see cref="ThreadMemoryRepository" /> against a real PostgreSQL instance with pgvector.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class ThreadMemoryRepositoryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private const string RepoId = "test-repo";
    private static readonly Guid ClientA = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid ClientB = Guid.Parse("cccccccc-0000-0000-0000-000000000002");

    private MeisterProPRDbContext _db = null!;
    private ThreadMemoryRepository _repo = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._db = new MeisterProPRDbContext(options);

        // Ensure clients exist
        foreach (var clientId in new[] { ClientA, ClientB })
        {
            if (!await this._db.Clients.AnyAsync(c => c.Id == clientId))
            {
                this._db.Clients.Add(
                    new ClientRecord
                    {
                        Id = clientId,
                        TenantId = TenantCatalog.SystemTenantId,
                        DisplayName = $"Memory Test Client {clientId:N}",
                        IsActive = true,
                        CreatedAt = DateTimeOffset.UtcNow,
                    });
            }
        }

        await this._db.SaveChangesAsync();

        // Clean state before each test run
        await this._db.ThreadMemoryRecords.ExecuteDeleteAsync();

        this._repo = new ThreadMemoryRepository(this._db);
    }

    public async Task DisposeAsync()
    {
        if (this._db is not null)
        {
            await this._db.DisposeAsync();
        }
    }

    [Fact]
    public async Task UpsertAsync_NewRecord_CreatesRow()
    {
        var record = CreateRecord(ClientA, 1, 101);

        await this._repo.UpsertAsync(record);

        var stored = await this._db.ThreadMemoryRecords
            .FirstOrDefaultAsync(r => r.ClientId == ClientA && r.ThreadId == 101);
        Assert.NotNull(stored);
        Assert.Equal("src/Foo.cs", stored.FilePath);
    }

    [Fact]
    public async Task UpsertAsync_RecordWithResolutionOutcome_RoundTripsBothClassifications()
    {
        var record = CreateRecord(
            ClientA,
            1,
            110,
            resolutionIntent: ThreadResolutionIntent.AcceptedByHuman,
            resolutionClarity: ResolutionClarity.AcceptedWithoutChange);

        await this._repo.UpsertAsync(record);

        var stored = await this._db.ThreadMemoryRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.ClientId == ClientA && r.ThreadId == 110);
        Assert.NotNull(stored);
        Assert.Equal(ThreadResolutionIntent.AcceptedByHuman, stored.ResolutionIntent);
        Assert.Equal(ResolutionClarity.AcceptedWithoutChange, stored.ResolutionClarity);
    }

    [Fact]
    public async Task UpsertAsync_ReclassifiedResolution_ReplacesTheStoredOutcome()
    {
        // A reviewer can reopen a thread and close it differently, and the resolution pipeline can re-run for
        // the same thread. The stored outcome has to follow the latest write rather than stay at the first.
        var claimedFix = CreateRecord(
            ClientA,
            1,
            113,
            resolutionIntent: ThreadResolutionIntent.ClaimsFix,
            resolutionClarity: ResolutionClarity.ResolvedByChange);
        await this._repo.UpsertAsync(claimedFix);

        var reclassified = CreateRecord(
            ClientA,
            1,
            113,
            resolutionIntent: ThreadResolutionIntent.AcceptedByHuman,
            resolutionClarity: ResolutionClarity.AcceptedWithoutChange);
        await this._repo.UpsertAsync(reclassified);

        var stored = await this._db.ThreadMemoryRecords
            .AsNoTracking()
            .SingleAsync(r => r.ClientId == ClientA && r.ThreadId == 113);
        Assert.Equal(ThreadResolutionIntent.AcceptedByHuman, stored.ResolutionIntent);
        Assert.Equal(ResolutionClarity.AcceptedWithoutChange, stored.ResolutionClarity);
    }

    [Fact]
    public async Task UpsertAsync_RecordWithoutResolutionOutcome_StoresItAsAbsent()
    {
        var record = CreateRecord(ClientA, 1, 111);

        await this._repo.UpsertAsync(record);

        var stored = await this._db.ThreadMemoryRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.ClientId == ClientA && r.ThreadId == 111);
        Assert.NotNull(stored);
        Assert.Null(stored.ResolutionIntent);
        Assert.Null(stored.ResolutionClarity);
    }

    [Fact]
    public async Task FindSimilarAsync_RecordWithResolutionOutcome_CarriesItIntoTheMatch()
    {
        var record = CreateRecord(
            ClientA,
            1,
            112,
            resolutionIntent: ThreadResolutionIntent.AcceptedByHuman,
            resolutionClarity: ResolutionClarity.Undetermined);
        await this._repo.UpsertAsync(record);

        var matches = await this._repo.FindSimilarAsync(ClientA, V(1f), 5, 0.5f);

        var match = Assert.Single(matches, m => m.ThreadId == 112);
        Assert.Equal(ThreadResolutionIntent.AcceptedByHuman, match.Intent);
        Assert.Equal(ResolutionClarity.Undetermined, match.Clarity);
    }

    [Fact]
    public async Task PullRequestScopedLookups_RecordWithResolutionOutcome_CarryItIntoTheMatch()
    {
        var record = CreateRecord(
            ClientA,
            88,
            114,
            filePath: "src/Outcome.cs",
            resolutionIntent: ThreadResolutionIntent.AcceptedByHuman,
            resolutionClarity: ResolutionClarity.AcceptedWithoutChange);
        await this._repo.UpsertAsync(record);

        var semantic = await this._repo.FindSimilarInPullRequestAsync(ClientA, RepoId, 88, V(1f), 5, 0.5f);
        var byPath = await this._repo.FindByPullRequestFilePathAsync(ClientA, RepoId, 88, "src/Outcome.cs", 5);

        var semanticMatch = Assert.Single(semantic, m => m.ThreadId == 114);
        Assert.Equal(ThreadResolutionIntent.AcceptedByHuman, semanticMatch.Intent);
        Assert.Equal(ResolutionClarity.AcceptedWithoutChange, semanticMatch.Clarity);

        var pathMatch = Assert.Single(byPath, m => m.ThreadId == 114);
        Assert.Equal(ThreadResolutionIntent.AcceptedByHuman, pathMatch.Intent);
        Assert.Equal(ResolutionClarity.AcceptedWithoutChange, pathMatch.Clarity);
    }

    [Fact]
    public async Task UpsertAsync_ExistingRecord_UpdatesRow()
    {
        var record1 = CreateRecord(ClientA, 1, 102);
        await this._repo.UpsertAsync(record1);

        var record2 = CreateRecord(ClientA, 1, 102);
        // Same key, different summary
        var updatedRecord = new ThreadMemoryRecord
        {
            Id = record1.Id,
            ClientId = record1.ClientId,
            ThreadId = record1.ThreadId,
            RepositoryId = record1.RepositoryId,
            PullRequestId = record1.PullRequestId,
            FilePath = record1.FilePath,
            ChangeExcerpt = record1.ChangeExcerpt,
            CommentHistoryDigest = record1.CommentHistoryDigest,
            ResolutionSummary = "Updated resolution.",
            EmbeddingVector = record1.EmbeddingVector,
            CreatedAt = record1.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await this._repo.UpsertAsync(updatedRecord);

        var stored = await this._db.ThreadMemoryRecords
            .Where(r => r.ClientId == ClientA && r.ThreadId == 102)
            .ToListAsync();
        Assert.Single(stored);
        Assert.Equal("Updated resolution.", stored[0].ResolutionSummary);
    }

    [Fact]
    public async Task BulkUpsertAsync_MultipleRecords_InsertsBatch()
    {
        var records = Enumerable.Range(200, 5).Select(i => CreateRecord(ClientA, 1, i)).ToList();

        await this._repo.BulkUpsertAsync(records);

        var count = await this._db.ThreadMemoryRecords.CountAsync(r =>
            r.ClientId == ClientA && r.ThreadId >= 200 && r.ThreadId < 205);
        Assert.Equal(5, count);
    }

    [Fact]
    public async Task BulkUpsertAsync_RerunWithSameRecords_IsIdempotent()
    {
        var records = Enumerable.Range(300, 3).Select(i => CreateRecord(ClientA, 1, i)).ToList();

        await this._repo.BulkUpsertAsync(records);
        await this._repo.BulkUpsertAsync(records); // second run — same keys

        var count = await this._db.ThreadMemoryRecords.CountAsync(r =>
            r.ClientId == ClientA && r.ThreadId >= 300 && r.ThreadId < 303);
        Assert.Equal(3, count);
    }

    [Fact]
    public async Task RemoveByThreadAsync_RecordExists_DeletesAndReturnsTrue()
    {
        await this._repo.UpsertAsync(CreateRecord(ClientA, 1, 400));

        var deleted = await this._repo.RemoveByThreadAsync(ClientA, RepoId, 400);

        Assert.True(deleted);
        Assert.False(await this._db.ThreadMemoryRecords.AnyAsync(r => r.ThreadId == 400 && r.ClientId == ClientA));
    }

    [Fact]
    public async Task RemoveByThreadAsync_NoRecord_ReturnsFalseWithoutError()
    {
        var deleted = await this._repo.RemoveByThreadAsync(ClientA, RepoId, 99999);
        Assert.False(deleted);
    }

    [Fact]
    public async Task FindSimilarAsync_AboveThreshold_ReturnsMatchesOrderedByScore()
    {
        // Seed two records: one very similar (near-identical vector), one dissimilar.
        var nearVector = V(1f, 0f, 0f, 0f);
        var farVector = V(0f, 0f, 0f, 1f);
        var queryVector = V(0.99f, 0.01f, 0f, 0f);

        await this._repo.UpsertAsync(CreateRecord(ClientA, 1, 501, nearVector));
        await this._repo.UpsertAsync(CreateRecord(ClientA, 1, 502, farVector));

        var results = await this._repo.FindSimilarAsync(ClientA, queryVector, 5, 0.7f);

        Assert.NotEmpty(results);
        Assert.Equal(501, results[0].ThreadId);
        Assert.True(results[0].SimilarityScore >= 0.7f);
        Assert.DoesNotContain(results, r => r.ThreadId == 502);
    }

    [Fact]
    public async Task FindSimilarAsync_HonoursTopNCap()
    {
        var queryVector = V(1f, 0f, 0f, 0f);
        var records = Enumerable.Range(600, 10)
            .Select(i =>
                CreateRecord(ClientA, 1, i, V(1f, 0f, 0f, 0f)))
            .ToList();
        await this._repo.BulkUpsertAsync(records);

        var results = await this._repo.FindSimilarAsync(ClientA, queryVector, 3, 0.5f);

        Assert.True(results.Count <= 3);
    }

    [Fact]
    public async Task FindSimilarAsync_NeverReturnsDifferentClientRecords()
    {
        var vector = V(1f, 0f, 0f, 0f);
        await this._repo.UpsertAsync(CreateRecord(ClientB, 1, 700, vector));

        var results = await this._repo.FindSimilarAsync(ClientA, vector, 10, 0.0f);

        Assert.DoesNotContain(results, r => r.ThreadId == 700);
    }

    [Fact]
    public async Task FindSimilarAsync_EmptyStore_ReturnsEmptyList()
    {
        var results = await this._repo.FindSimilarAsync(ClientA, V(1f, 0f, 0f, 0f), 5, 0.5f);
        Assert.Empty(results);
    }

    [Fact]
    public async Task FindSimilarAsync_WithDbContextFactory_SupportsParallelLookups()
    {
        var options = fixture.ConnectionString;
        Assert.False(string.IsNullOrWhiteSpace(options));

        var dbOptions = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(options, o => o.UseVector())
            .Options;
        var factory = new PooledDbContextFactory<MeisterProPRDbContext>(dbOptions);
        var repo = new ThreadMemoryRepository(this._db, factory);

        var nearVector = V(1f, 0f, 0f, 0f);
        var queryVector = V(0.99f, 0.01f, 0f, 0f);
        await repo.UpsertAsync(CreateRecord(ClientA, 1, 503, nearVector));

        var tasks = Enumerable.Range(0, 4)
            .Select(_ => repo.FindSimilarAsync(ClientA, queryVector, 5, 0.7f));

        var results = await Task.WhenAll(tasks);

        Assert.All(results, matches => Assert.Equal(503, Assert.Single(matches).ThreadId));
    }

    [Fact]
    public async Task FindByFilePathAsync_ReturnsSameRepoExactPathMatchesOrderedByUpdatedAt()
    {
        var older = CreateRecord(
            ClientA,
            1,
            801,
            filePath: "package.json",
            updatedAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        var newer = CreateRecord(ClientA, 1, 802, filePath: "package.json", updatedAt: DateTimeOffset.UtcNow);
        var otherRepo = CreateRecord(
            ClientA,
            1,
            803,
            filePath: "package.json",
            repositoryId: "other-repo",
            updatedAt: DateTimeOffset.UtcNow.AddMinutes(5));
        var otherPath = CreateRecord(
            ClientA,
            1,
            804,
            filePath: "vite.config.js",
            updatedAt: DateTimeOffset.UtcNow.AddMinutes(6));

        await this._repo.UpsertAsync(older);
        await this._repo.UpsertAsync(newer);
        await this._repo.UpsertAsync(otherRepo);
        await this._repo.UpsertAsync(otherPath);

        var results = await this._repo.FindByFilePathAsync(ClientA, RepoId, "package.json", 5);

        Assert.Collection(
            results,
            first =>
            {
                Assert.Equal(802, first.ThreadId);
                Assert.Equal("exact_file_fallback", first.MatchSource);
            },
            second =>
            {
                Assert.Equal(801, second.ThreadId);
                Assert.Equal("exact_file_fallback", second.MatchSource);
            });
        Assert.All(results, r => Assert.Equal("package.json", r.FilePath));
        Assert.DoesNotContain(results, r => r.ThreadId == 803 || r.ThreadId == 804);
    }

    [Fact]
    public async Task FindByFilePathAsync_IsCaseInsensitive()
    {
        var record = CreateRecord(ClientA, 1, 805, filePath: "SRC/Package.JSON");
        await this._repo.UpsertAsync(record);

        var results = await this._repo.FindByFilePathAsync(ClientA, RepoId, "src/package.json", 5);

        Assert.Single(results);
        Assert.Equal(805, results[0].ThreadId);
    }

    [Fact]
    public async Task FindByPullRequestFilePathAsync_IsCaseInsensitive()
    {
        var record = CreateRecord(ClientA, 77, 806, filePath: "SRC/Package.JSON");
        await this._repo.UpsertAsync(record);

        var results = await this._repo.FindByPullRequestFilePathAsync(ClientA, RepoId, 77, "src/package.json", 5);

        Assert.Single(results);
        Assert.Equal(806, results[0].ThreadId);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 0)]
    public async Task GetPagedAsync_InvalidPagination_Throws(int page, int pageSize)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            this._repo.GetPagedAsync(ClientA, null, page, pageSize));
    }

    [Fact]
    public async Task FindSimilarAsync_InvalidTopN_Throws()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            this._repo.FindSimilarAsync(ClientA, V(1f), 0, 0.5f));
    }

    [Fact]
    public async Task FindSimilarAsync_InvalidSimilarity_Throws()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            this._repo.FindSimilarAsync(ClientA, V(1f), 1, 1.5f));
    }

    [Fact]
    public async Task FindSimilarInPullRequestAsync_InvalidSimilarity_Throws()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            this._repo.FindSimilarInPullRequestAsync(ClientA, RepoId, 1, V(1f), 1, -0.1f));
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    // pgvector columns are created with vector(1536) in the migration.
    // All test vectors must have exactly 1536 dimensions.
    private static float[] V(params float[] seed)
    {
        var v = new float[1536];
        for (var i = 0; i < seed.Length && i < v.Length; i++)
        {
            v[i] = seed[i];
        }

        return v;
    }

    private static ThreadMemoryRecord CreateRecord(
        Guid clientId,
        int prId,
        int threadId,
        float[]? vector = null,
        string filePath = "src/Foo.cs",
        string repositoryId = RepoId,
        DateTimeOffset? updatedAt = null,
        ThreadResolutionIntent? resolutionIntent = null,
        ResolutionClarity? resolutionClarity = null)
    {
        var timestamp = updatedAt ?? DateTimeOffset.UtcNow;

        return new ThreadMemoryRecord
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ThreadId = threadId,
            RepositoryId = repositoryId,
            PullRequestId = prId,
            FilePath = filePath,
            ChangeExcerpt = "- old\n+ new",
            CommentHistoryDigest = "Reviewer: fix this please\nAuthor: done",
            ResolutionSummary = "The issue was resolved by changing X.",
            EmbeddingVector = vector ?? V(1f),
            ResolutionIntent = resolutionIntent,
            ResolutionClarity = resolutionClarity,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
        };
    }
}
