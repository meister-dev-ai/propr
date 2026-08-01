// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Repositories;

/// <summary>
///     EF Core plus pgvector implementation of <see cref="IPostedFindingRepository" />.
///     All <c>float[]</c> to <see cref="Vector" /> conversion is contained within this class.
/// </summary>
public sealed class PostedFindingRepository(
    MeisterProPRDbContext db,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null) : IPostedFindingRepository
{
    /// <inheritdoc />
    public async Task<PostedFindingSimilarityDto?> FindClosestInPullRequestAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        float[] queryVector,
        float minSimilarity,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(queryVector);

        if (queryVector.Length == 0)
        {
            throw new ArgumentException("Query vector must contain at least one dimension.", nameof(queryVector));
        }

        if (minSimilarity is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minSimilarity),
                minSimilarity,
                "MinSimilarity must be between 0 and 1.");
        }

        var pgVector = new Vector(queryVector);

        // Expressed as a distance ceiling rather than a similarity floor so the comparison, the ordering and
        // the limit all happen in the database and the HNSW index can serve the query.
        var maxDistance = 1.0 - minSimilarity;

        var closest = await this.WithDbAsync(
            innerDb => innerDb.PostedFindingRecords
                .AsNoTracking()
                .Where(r =>
                    r.ClientId == clientId &&
                    r.RepositoryId == repositoryId &&
                    r.PullRequestId == pullRequestId)
                .Select(r => new
                {
                    r.Id,
                    r.ProviderThreadId,
                    r.AutoResolvedByProPr,
                    Distance = r.EmbeddingVector.CosineDistance(pgVector),
                })
                .Where(r => r.Distance <= maxDistance)
                .OrderBy(r => r.Distance)
                .FirstOrDefaultAsync(ct),
            ct);

        return closest is null
            ? null
            : new PostedFindingSimilarityDto(
                closest.Id,
                closest.ProviderThreadId,
                (float)(1.0 - closest.Distance),
                closest.AutoResolvedByProPr);
    }

    /// <inheritdoc />
    public async Task AddMissingAsync(IReadOnlyList<PostedFindingRecord> records, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);

        if (records.Count == 0)
        {
            return;
        }

        await this.WithDbAsync(
            async innerDb =>
            {
                // One row per posted thread. A republished pass re-offers threads it already indexed, and the
                // probe cannot see rows added but not yet saved in this batch, so both are filtered. The scope
                // is taken per group rather than from the first record: a thread id is only unique within one
                // pull request, so probing a mixed batch under one scope would both miss existing rows and
                // discard legitimate new ones.
                var seen = new HashSet<(Guid ClientId, string RepositoryId, int PullRequestId, long ThreadId)>();
                var added = 0;

                foreach (var scope in records.GroupBy(r => new { r.ClientId, r.RepositoryId, r.PullRequestId }))
                {
                    var clientId = scope.Key.ClientId;
                    var repositoryId = scope.Key.RepositoryId;
                    var pullRequestId = scope.Key.PullRequestId;
                    var candidateThreadIds = scope.Select(r => r.ProviderThreadId).Distinct().ToList();

                    var alreadyIndexed = await innerDb.PostedFindingRecords
                        .AsNoTracking()
                        .Where(r =>
                            r.ClientId == clientId &&
                            r.RepositoryId == repositoryId &&
                            r.PullRequestId == pullRequestId &&
                            candidateThreadIds.Contains(r.ProviderThreadId))
                        .Select(r => r.ProviderThreadId)
                        .ToListAsync(ct);

                    foreach (var threadId in alreadyIndexed)
                    {
                        seen.Add((clientId, repositoryId, pullRequestId, threadId));
                    }
                }

                foreach (var record in records)
                {
                    var key = (record.ClientId, record.RepositoryId, record.PullRequestId, record.ProviderThreadId);
                    if (!seen.Add(key))
                    {
                        continue;
                    }

                    record.Validate();
                    innerDb.PostedFindingRecords.Add(record);
                    added++;
                }

                if (added > 0)
                {
                    await innerDb.SaveChangesAsync(ct);
                }

                return added;
            },
            ct);
    }

    private async Task<TResult> WithDbAsync<TResult>(
        Func<MeisterProPRDbContext, Task<TResult>> operation,
        CancellationToken ct)
    {
        if (contextFactory is null)
        {
            return await operation(db);
        }

        await using var shortLivedDb = await contextFactory.CreateDbContextAsync(ct);
        return await operation(shortLivedDb);
    }
}
