// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using MeisterDev.ProPR.Infrastructure.Data.Models;
using MeisterDev.ProPR.Infrastructure.Features.IdentityAndAccess;
using MeisterDev.ProPR.Infrastructure.Repositories;
using MeisterDev.ProPR.TestSupport;
using Microsoft.EntityFrameworkCore;
using FactAttribute = Xunit.SkippableFactAttribute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Repositories;

/// <summary>
///     Integration tests for <see cref="PostedFindingRepository" /> against a real PostgreSQL instance with
///     pgvector. The cosine search is what decides whether a concern already raised on a pull request comes
///     back, so its scoping and its threshold are exercised against the real query planner.
/// </summary>
[Collection("PostgresIntegration")]
public sealed class PostedFindingRepositoryTests(PostgresContainerFixture fixture) : IAsyncLifetime
{
    private const string RepoId = "posted-finding-repo";
    private static readonly Guid ClientA = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid ClientB = Guid.Parse("dddddddd-0000-0000-0000-000000000002");

    private MeisterProPRDbContext _db = null!;
    private PostedFindingRepository _repo = null!;

    public async Task InitializeAsync()
    {
        fixture.SkipIfUnavailable();

        var options = new DbContextOptionsBuilder<MeisterProPRDbContext>()
            .UseNpgsql(fixture.ConnectionString, o => o.UseVector())
            .Options;
        this._db = new MeisterProPRDbContext(options);

        foreach (var clientId in new[] { ClientA, ClientB })
        {
            if (!await this._db.Clients.AnyAsync(c => c.Id == clientId))
            {
                this._db.Clients.Add(
                    new ClientRecord
                    {
                        Id = clientId,
                        TenantId = TenantCatalog.SystemTenantId,
                        DisplayName = $"Posted Finding Test Client {clientId:N}",
                        IsActive = true,
                        CreatedAt = DateTimeOffset.UtcNow,
                    });
            }
        }

        await this._db.SaveChangesAsync();

        await this._db.PostedFindingRecords
            .Where(r => r.ClientId == ClientA || r.ClientId == ClientB)
            .ExecuteDeleteAsync();

        this._repo = new PostedFindingRepository(this._db);
    }

    public async Task DisposeAsync()
    {
        if (this._db is not null)
        {
            await this._db.DisposeAsync();
        }
    }

    [Fact]
    public async Task FindClosestInPullRequestAsync_IdenticalVector_ReturnsTheThreadAndScore()
    {
        await this._repo.AddMissingAsync([Record(ClientA, 22092, "101", V(1f, 0f))]);

        var match = await this._repo.FindClosestInPullRequestAsync(ClientA, RepoId, 22092, V(1f, 0f), 0.85f);

        Assert.NotNull(match);
        Assert.Equal("101", match.ProviderThreadId);
        Assert.True(match.SimilarityScore > 0.99f, $"similarity was {match.SimilarityScore}");
    }

    [Fact]
    public async Task FindClosestInPullRequestAsync_BelowTheThreshold_ReturnsNothing()
    {
        // An orthogonal vector is similarity 0, which is what a genuinely unrelated finding looks like.
        await this._repo.AddMissingAsync([Record(ClientA, 22092, "102", V(1f, 0f))]);

        var match = await this._repo.FindClosestInPullRequestAsync(ClientA, RepoId, 22092, V(0f, 1f), 0.85f);

        Assert.Null(match);
    }

    [Fact]
    public async Task FindClosestInPullRequestAsync_NeverCrossesClientRepositoryOrPullRequest()
    {
        await this._repo.AddMissingAsync(
        [
            Record(ClientB, 22092, "201", V(1f, 0f)),
            Record(ClientA, 22092, "202", V(1f, 0f), repositoryId: "other-repo"),
            Record(ClientA, 99999, "203", V(1f, 0f)),
        ]);

        var match = await this._repo.FindClosestInPullRequestAsync(ClientA, RepoId, 22092, V(1f, 0f), 0.85f);

        Assert.Null(match);
    }

    [Fact]
    public async Task FindClosestInPullRequestAsync_SeveralAboveTheThreshold_ReturnsTheClosest()
    {
        await this._repo.AddMissingAsync(
        [
            Record(ClientA, 22092, "301", V(1f, 0f)),
            Record(ClientA, 22092, "302", V(0.9f, 0.1f)),
        ]);

        var match = await this._repo.FindClosestInPullRequestAsync(ClientA, RepoId, 22092, V(1f, 0f), 0.5f);

        Assert.NotNull(match);
        Assert.Equal("301", match.ProviderThreadId);
    }

    [Fact]
    public async Task AddMissingAsync_ThreadAlreadyIndexed_DoesNotInsertASecondRow()
    {
        await this._repo.AddMissingAsync([Record(ClientA, 22092, "401", V(1f, 0f))]);
        await this._repo.AddMissingAsync([Record(ClientA, 22092, "401", V(0f, 1f))]);

        var rows = await this._db.PostedFindingRecords
            .AsNoTracking()
            .Where(r => r.ClientId == ClientA && r.ProviderThreadId == "401")
            .ToListAsync();

        Assert.Single(rows);
    }

    [Fact]
    public async Task AddMissingAsync_BatchSpanningPullRequests_KeepsBothRowsForTheSameThreadId()
    {
        // A provider thread id is only unique within one pull request. Scoping the whole batch by its first
        // record would treat the second pull request's identically numbered thread as already indexed.
        await this._repo.AddMissingAsync(
        [
            Record(ClientA, 22092, "403", V(1f, 0f)),
            Record(ClientA, 33033, "403", V(0f, 1f)),
        ]);

        var rows = await this._db.PostedFindingRecords
            .AsNoTracking()
            .Where(r => r.ClientId == ClientA && r.ProviderThreadId == "403")
            .ToListAsync();

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task AddMissingAsync_SameThreadTwiceInOneBatch_InsertsItOnce()
    {
        // The probe cannot see a row added but not yet saved, so the in-batch duplicate has to be filtered too
        // or the unique index rejects the whole save.
        await this._repo.AddMissingAsync(
        [
            Record(ClientA, 22092, "402", V(1f, 0f)),
            Record(ClientA, 22092, "402", V(0f, 1f)),
        ]);

        var rows = await this._db.PostedFindingRecords
            .AsNoTracking()
            .Where(r => r.ClientId == ClientA && r.ProviderThreadId == "402")
            .ToListAsync();

        Assert.Single(rows);
    }

    private static float[] V(float first, float second)
    {
        // The column is fixed at the production embedding width, so pad a readable two-dimensional intent out
        // to full length.
        var vector = new float[1536];
        vector[0] = first;
        vector[1] = second;
        return vector;
    }

    private static PostedFindingRecord Record(
        Guid clientId,
        int pullRequestId,
        string providerThreadId,
        float[] vector,
        string repositoryId = RepoId)
    {
        return new PostedFindingRecord
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            RepositoryId = repositoryId,
            PullRequestId = pullRequestId,
            ProviderThreadId = providerThreadId,
            ReviewJobId = Guid.NewGuid(),
            IterationId = 1,
            FilePath = "/src/Agents.cs",
            Severity = CommentSeverity.Error,
            FindingMessage = "The delete path re-checks ownership after the fetch.",
            EmbeddingVector = vector,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }
}
