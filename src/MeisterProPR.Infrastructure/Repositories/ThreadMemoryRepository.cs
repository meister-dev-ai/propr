// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>
///     EF Core + pgvector implementation of <see cref="IThreadMemoryRepository" />.
///     All <c>float[]</c> ↔ <see cref="Vector" /> conversions are contained within this class.
/// </summary>
public sealed class ThreadMemoryRepository(MeisterProPRDbContext db) : IThreadMemoryRepository
{
    private const int UpsertColumnCount = 13;

    /// <inheritdoc />
    public async Task UpsertAsync(ThreadMemoryRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await this.ExecuteBulkUpsertAsync([record], ct);
    }

    /// <inheritdoc />
    public async Task BulkUpsertAsync(IEnumerable<ThreadMemoryRecord> records, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(records);

        var materialized = records.ToList();
        if (materialized.Count == 0)
        {
            return;
        }

        var deduplicated = materialized
            .GroupBy(r => new { r.ClientId, r.RepositoryId, r.ThreadId })
            .Select(group => group.Last())
            .ToList();

        await this.ExecuteBulkUpsertAsync(deduplicated, ct);
    }

    private async Task ExecuteBulkUpsertAsync(IReadOnlyList<ThreadMemoryRecord> records, CancellationToken ct)
    {
        var (sql, parameters) = BuildBulkUpsertCommand(records);
        await db.Database.ExecuteSqlRawAsync(sql, parameters, ct);
    }

    private static (string Sql, object[] Parameters) BuildBulkUpsertCommand(IReadOnlyList<ThreadMemoryRecord> records)
    {
        var sql = new StringBuilder();
        var parameters = new List<object?>(records.Count * UpsertColumnCount);
        var valueTuples = new List<string>(records.Count);

        sql.AppendLine("""
            INSERT INTO thread_memory_records
                (id, client_id, thread_id, repository_id, pull_request_id, file_path,
                 change_excerpt, comment_history_digest, resolution_summary, embedding_vector,
                 memory_source, created_at, updated_at)
            VALUES
            """);

        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            var parameterOffset = index * UpsertColumnCount;
            valueTuples.Add(
                $"    ({{{parameterOffset}}}, {{{parameterOffset + 1}}}, {{{parameterOffset + 2}}}, {{{parameterOffset + 3}}}, {{{parameterOffset + 4}}}, {{{parameterOffset + 5}}}, {{{parameterOffset + 6}}}, {{{parameterOffset + 7}}}, {{{parameterOffset + 8}}}, {{{parameterOffset + 9}}}, {{{parameterOffset + 10}}}, {{{parameterOffset + 11}}}, {{{parameterOffset + 12}}})");
            parameters.AddRange(
                [
                    record.Id,
                    record.ClientId,
                    record.ThreadId,
                    record.RepositoryId,
                    record.PullRequestId,
                    record.FilePath,
                    record.ChangeExcerpt,
                    record.CommentHistoryDigest,
                    record.ResolutionSummary,
                    new Vector(record.EmbeddingVector),
                    (short)record.MemorySource,
                    record.CreatedAt,
                    record.UpdatedAt,
                ]);
        }

        sql.AppendLine(string.Join(",\n", valueTuples));
        sql.AppendLine("""
            ON CONFLICT (client_id, repository_id, thread_id) DO UPDATE SET
                pull_request_id        = EXCLUDED.pull_request_id,
                file_path              = EXCLUDED.file_path,
                change_excerpt         = EXCLUDED.change_excerpt,
                comment_history_digest = EXCLUDED.comment_history_digest,
                resolution_summary     = EXCLUDED.resolution_summary,
                embedding_vector       = EXCLUDED.embedding_vector,
                memory_source          = EXCLUDED.memory_source,
                updated_at             = EXCLUDED.updated_at
            """);

        return (sql.ToString(), parameters.Select(parameter => parameter!).ToArray());
    }

    /// <inheritdoc />
    public async Task<bool> RemoveByThreadAsync(
        Guid clientId,
        string repositoryId,
        int threadId,
        CancellationToken ct = default)
    {
        var deleted = await db.ThreadMemoryRecords
            .Where(r =>
                r.ClientId == clientId &&
                r.RepositoryId == repositoryId &&
                r.ThreadId == threadId)
            .ExecuteDeleteAsync(ct);

        return deleted > 0;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveByIdAsync(Guid id, Guid clientId, CancellationToken ct = default)
    {
        var deleted = await db.ThreadMemoryRecords
            .Where(r => r.Id == id && r.ClientId == clientId)
            .ExecuteDeleteAsync(ct);

        return deleted > 0;
    }

    /// <inheritdoc />
    public async Task<PagedResult<ThreadMemoryRecord>> GetPagedAsync(
        Guid clientId,
        string? search,
        int page,
        int pageSize,
        MemorySource? source = null,
        string? repositoryId = null,
        int? pullRequestId = null,
        CancellationToken ct = default)
    {
        if (page < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(page), page, "Page must be at least 1.");
        }

        if (pageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "PageSize must be at least 1.");
        }

        var query = db.ThreadMemoryRecords
            .AsNoTracking()
            .Where(r => r.ClientId == clientId);

        if (source.HasValue)
        {
            query = query.Where(r => r.MemorySource == source.Value);
        }

        if (repositoryId is not null)
        {
            query = query.Where(r => r.RepositoryId == repositoryId);
        }

        if (pullRequestId.HasValue)
        {
            query = query.Where(r => r.PullRequestId == pullRequestId.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchPattern = $"%{EscapeLikePattern(search)}%";
            query = query.Where(r =>
                (r.FilePath != null && EF.Functions.ILike(r.FilePath, searchPattern)) ||
                EF.Functions.ILike(r.RepositoryId, searchPattern) ||
                EF.Functions.ILike(r.ResolutionSummary, searchPattern));
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(r => r.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<ThreadMemoryRecord>(items, total, page, pageSize);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ThreadMemoryMatchDto>> FindSimilarAsync(
        Guid clientId,
        float[] queryVector,
        int topN,
        float minSimilarity,
        CancellationToken ct = default)
    {
        ValidateSimilarityArguments(queryVector, topN, minSimilarity);

        var pgVector = new Vector(queryVector);
        var maxDistance = 1.0 - minSimilarity;

        // Query using pgvector cosine distance; 1 - distance = cosine similarity.
        var results = await db.ThreadMemoryRecords
            .Where(r => r.ClientId == clientId)
            .Select(r => new
            {
                r.Id,
                r.ThreadId,
                r.FilePath,
                r.ResolutionSummary,
                r.MemorySource,
                Distance = r.EmbeddingVector.CosineDistance(pgVector),
            })
            .Where(r => r.Distance <= maxDistance)
            .OrderBy(r => r.Distance)
            .Take(topN)
            .ToListAsync(ct);

        return results
            .Select(r => new ThreadMemoryMatchDto(
                r.Id,
                r.ThreadId,
                r.FilePath,
                r.ResolutionSummary,
                (float)(1.0 - r.Distance),
                Source: r.MemorySource))
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ThreadMemoryMatchDto>> FindByFilePathAsync(
        Guid clientId,
        string repositoryId,
        string filePath,
        int topN,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return [];
        }

        var exactPattern = EscapeLikePattern(filePath);

        return await db.ThreadMemoryRecords
            .AsNoTracking()
            .Where(r =>
                r.ClientId == clientId &&
                r.RepositoryId == repositoryId &&
                r.FilePath != null &&
                EF.Functions.ILike(r.FilePath, exactPattern))
            .OrderByDescending(r => r.UpdatedAt)
            .Take(topN)
            .Select(r => new ThreadMemoryMatchDto(
                r.Id,
                r.ThreadId,
                r.FilePath,
                r.ResolutionSummary,
                0f,
                "exact_file_fallback",
                r.MemorySource))
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ThreadMemoryMatchDto>> FindSimilarInPullRequestAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        float[] queryVector,
        int topN,
        float minSimilarity,
        CancellationToken ct = default)
    {
        ValidateSimilarityArguments(queryVector, topN, minSimilarity);

        var pgVector = new Vector(queryVector);
        var maxDistance = 1.0 - minSimilarity;

        var results = await db.ThreadMemoryRecords
            .Where(r =>
                r.ClientId == clientId &&
                r.RepositoryId == repositoryId &&
                r.PullRequestId == pullRequestId)
            .Select(r => new
            {
                r.Id,
                r.ThreadId,
                r.FilePath,
                r.ResolutionSummary,
                r.MemorySource,
                Distance = r.EmbeddingVector.CosineDistance(pgVector),
            })
            .Where(r => r.Distance <= maxDistance)
            .OrderBy(r => r.Distance)
            .Take(topN)
            .ToListAsync(ct);

        return results
            .Select(r => new ThreadMemoryMatchDto(
                r.Id,
                r.ThreadId,
                r.FilePath,
                r.ResolutionSummary,
                (float)(1.0 - r.Distance),
                Source: r.MemorySource))
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ThreadMemoryMatchDto>> FindByPullRequestFilePathAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        string filePath,
        int topN,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return [];
        }

        var exactPattern = EscapeLikePattern(filePath);

        return await db.ThreadMemoryRecords
            .AsNoTracking()
            .Where(r =>
                r.ClientId == clientId &&
                r.RepositoryId == repositoryId &&
                r.PullRequestId == pullRequestId &&
                r.FilePath != null &&
                EF.Functions.ILike(r.FilePath, exactPattern))
            .OrderByDescending(r => r.UpdatedAt)
            .Take(topN)
            .Select(r => new ThreadMemoryMatchDto(
                r.Id,
                r.ThreadId,
                r.FilePath,
                r.ResolutionSummary,
                0f,
                "exact_file_fallback",
                r.MemorySource))
            .ToListAsync(ct);
    }

    private static void ValidateSimilarityArguments(float[] queryVector, int topN, float minSimilarity)
    {
        if (queryVector.Length == 0)
        {
            throw new ArgumentException("Query vector must contain at least one dimension.", nameof(queryVector));
        }

        if (topN < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(topN), topN, "TopN must be at least 1.");
        }

        if (minSimilarity < 0f || minSimilarity > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(minSimilarity), minSimilarity, "MinSimilarity must be between 0 and 1.");
        }
    }

    private static string EscapeLikePattern(string value)
    {
        return value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    }
}
