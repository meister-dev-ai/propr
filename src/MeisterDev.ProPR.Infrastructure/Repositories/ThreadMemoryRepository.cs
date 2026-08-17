// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Repositories;

/// <summary>
///     EF Core + pgvector implementation of <see cref="IThreadMemoryRepository" />.
///     All <c>float[]</c> ↔ <see cref="Vector" /> conversions are contained within this class.
/// </summary>
public sealed class ThreadMemoryRepository(
    MeisterProPRDbContext db,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null) : IThreadMemoryRepository
{
    private const int UpsertColumnCount = 17;

    /// <summary>
    ///     Selects the display fields of a memory record and nothing else. Naming the columns keeps the
    ///     1536-dimension embedding — the bulk of the stored row — out of the result set entirely.
    /// </summary>
    private static readonly System.Linq.Expressions.Expression<Func<ThreadMemoryRecord, ThreadMemoryDigestDto>>
        DigestProjection = r => new ThreadMemoryDigestDto(
            r.Id,
            r.ThreadId,
            r.MemorySource,
            r.RepositoryId,
            r.PullRequestId,
            r.FilePath,
            r.ResolutionSummary,
            r.UpdatedAt,
            r.ResolutionIntent,
            r.ResolutionClarity);

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

    /// <inheritdoc />
    public async Task<bool> RemoveByThreadAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        string threadId,
        CancellationToken ct = default)
    {
        var deleted = await this.WithDbAsync(
            innerDb => innerDb.ThreadMemoryRecords
                .Where(r =>
                    r.ClientId == clientId &&
                    r.OrganizationUrl == organizationUrl &&
                    r.ProjectId == projectId &&
                    r.RepositoryId == repositoryId &&
                    r.ThreadId == threadId)
                .ExecuteDeleteAsync(ct),
            ct);

        return deleted > 0;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveByIdAsync(Guid id, Guid clientId, CancellationToken ct = default)
    {
        var deleted = await this.WithDbAsync(
            innerDb => innerDb.ThreadMemoryRecords
                .Where(r => r.Id == id && r.ClientId == clientId)
                .ExecuteDeleteAsync(ct),
            ct);

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

        return await this.WithDbAsync(
            async innerDb =>
            {
                var query = innerDb.ThreadMemoryRecords
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
                    var keywordTerm = search.Trim().ToLowerInvariant();

                    // Keywords are matched additively alongside the existing columns. They exist so an operator
                    // who remembers roughly what a decision was about can find it without an embedding query;
                    // the similarity-matching path is a different query entirely and is untouched by this.
                    query = query.Where(r =>
                        (r.FilePath != null && EF.Functions.ILike(r.FilePath, searchPattern)) ||
                        EF.Functions.ILike(r.RepositoryId, searchPattern) ||
                        EF.Functions.ILike(r.ResolutionSummary, searchPattern) ||
                        r.Keywords.Contains(keywordTerm));
                }

                var total = await query.CountAsync(ct);
                var items = await query
                    .OrderByDescending(r => r.UpdatedAt)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToListAsync(ct);

                return new PagedResult<ThreadMemoryRecord>(items, total, page, pageSize);
            },
            ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ThreadMemoryDigestDto>> GetDigestsByIdsAsync(
        Guid clientId,
        IReadOnlyCollection<Guid> ids,
        CancellationToken ct = default)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var distinctIds = ids.Distinct().ToArray();

        return await this.WithDbAsync(
            async innerDb => (IReadOnlyList<ThreadMemoryDigestDto>)await innerDb.ThreadMemoryRecords
                .AsNoTracking()
                .Where(r => r.ClientId == clientId && distinctIds.Contains(r.Id))
                .Select(DigestProjection)
                .ToListAsync(ct)
                .ConfigureAwait(false),
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<PagedResult<ThreadMemoryDigestDto>> GetDigestsForPullRequestAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        MemorySource source,
        int limit,
        CancellationToken ct = default)
    {
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "Limit must be at least 1.");
        }

        return await this.WithDbAsync(
            async innerDb =>
            {
                var query = innerDb.ThreadMemoryRecords
                    .AsNoTracking()
                    .Where(r => r.ClientId == clientId
                                && r.OrganizationUrl == organizationUrl
                                && r.ProjectId == projectId
                                && r.RepositoryId == repositoryId
                                && r.PullRequestId == pullRequestId
                                && r.MemorySource == source);

                var total = await query.CountAsync(ct).ConfigureAwait(false);
                var items = await query
                    .OrderByDescending(r => r.UpdatedAt)
                    .Take(limit)
                    .Select(DigestProjection)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                return new PagedResult<ThreadMemoryDigestDto>(items, total, 1, limit);
            },
            ct).ConfigureAwait(false);
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

        var results = await this.WithDbAsync(
            innerDb => innerDb.ThreadMemoryRecords
                .Where(r => r.ClientId == clientId)
                .Select(r => new
                {
                    r.Id,
                    r.ThreadId,
                    r.FilePath,
                    r.ResolutionSummary,
                    r.MemorySource,
                    r.ResolutionIntent,
                    r.ResolutionClarity,
                    Distance = r.EmbeddingVector.CosineDistance(pgVector),
                })
                .Where(r => r.Distance <= maxDistance)
                .OrderBy(r => r.Distance)
                .Take(topN)
                .ToListAsync(ct),
            ct);

        return results
            .Select(r => new ThreadMemoryMatchDto(
                r.Id,
                r.ThreadId,
                r.FilePath,
                r.ResolutionSummary,
                (float)(1.0 - r.Distance),
                Source: r.MemorySource,
                Intent: r.ResolutionIntent,
                Clarity: r.ResolutionClarity))
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ThreadMemoryMatchDto>> FindByFilePathAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
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

        return await this.WithDbAsync(
            innerDb => innerDb.ThreadMemoryRecords
                .AsNoTracking()
                .Where(r =>
                    r.ClientId == clientId &&
                    r.OrganizationUrl == organizationUrl &&
                    r.ProjectId == projectId &&
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
                    r.MemorySource,
                    r.ResolutionIntent,
                    r.ResolutionClarity))
                .ToListAsync(ct),
            ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ThreadMemoryMatchDto>> FindSimilarInPullRequestAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
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

        var results = await this.WithDbAsync(
            innerDb => innerDb.ThreadMemoryRecords
                .Where(r =>
                    r.ClientId == clientId &&
                    r.OrganizationUrl == organizationUrl &&
                    r.ProjectId == projectId &&
                    r.RepositoryId == repositoryId &&
                    r.PullRequestId == pullRequestId)
                .Select(r => new
                {
                    r.Id,
                    r.ThreadId,
                    r.FilePath,
                    r.ResolutionSummary,
                    r.MemorySource,
                    r.ResolutionIntent,
                    r.ResolutionClarity,
                    Distance = r.EmbeddingVector.CosineDistance(pgVector),
                })
                .Where(r => r.Distance <= maxDistance)
                .OrderBy(r => r.Distance)
                .Take(topN)
                .ToListAsync(ct),
            ct);

        return results
            .Select(r => new ThreadMemoryMatchDto(
                r.Id,
                r.ThreadId,
                r.FilePath,
                r.ResolutionSummary,
                (float)(1.0 - r.Distance),
                Source: r.MemorySource,
                Intent: r.ResolutionIntent,
                Clarity: r.ResolutionClarity))
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ThreadMemoryMatchDto>> FindByPullRequestFilePathAsync(
        Guid clientId,
        string organizationUrl,
        string projectId,
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

        return await this.WithDbAsync(
            innerDb => innerDb.ThreadMemoryRecords
                .AsNoTracking()
                .Where(r =>
                    r.ClientId == clientId &&
                    r.OrganizationUrl == organizationUrl &&
                    r.ProjectId == projectId &&
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
                    r.MemorySource,
                    r.ResolutionIntent,
                    r.ResolutionClarity))
                .ToListAsync(ct),
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

    private async Task ExecuteBulkUpsertAsync(IReadOnlyList<ThreadMemoryRecord> records, CancellationToken ct)
    {
        var (sql, parameters) = BuildBulkUpsertCommand(records);
        await this.WithDbAsync(innerDb => innerDb.Database.ExecuteSqlRawAsync(sql, parameters, ct), ct);
    }

    private static (string Sql, object[] Parameters) BuildBulkUpsertCommand(IReadOnlyList<ThreadMemoryRecord> records)
    {
        var sql = new StringBuilder();
        var parameters = new List<object?>(records.Count * UpsertColumnCount);
        var valueTuples = new List<string>(records.Count);

        sql.AppendLine(
            """
            INSERT INTO thread_memory_records
                (id, client_id, organization_url, project_id, thread_id, repository_id, pull_request_id, file_path,
                 change_excerpt, comment_history_digest, resolution_summary, embedding_vector,
                 memory_source, resolution_intent, resolution_clarity, created_at, updated_at)
            VALUES
            """);

        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            var parameterOffset = index * UpsertColumnCount;
            valueTuples.Add(
                $"    ({{{parameterOffset}}}, {{{parameterOffset + 1}}}, {{{parameterOffset + 2}}}, {{{parameterOffset + 3}}}, {{{parameterOffset + 4}}}, {{{parameterOffset + 5}}}, {{{parameterOffset + 6}}}, {{{parameterOffset + 7}}}, {{{parameterOffset + 8}}}, {{{parameterOffset + 9}}}, {{{parameterOffset + 10}}}, {{{parameterOffset + 11}}}, {{{parameterOffset + 12}}}, {{{parameterOffset + 13}}}, {{{parameterOffset + 14}}}, {{{parameterOffset + 15}}}, {{{parameterOffset + 16}}})");
            parameters.AddRange(
            [
                record.Id,
                record.ClientId,
                record.OrganizationUrl,
                record.ProjectId,
                record.ThreadId,
                record.RepositoryId,
                record.PullRequestId,
                record.FilePath,
                record.ChangeExcerpt,
                record.CommentHistoryDigest,
                record.ResolutionSummary,
                new Vector(record.EmbeddingVector),
                (short)record.MemorySource,
                record.ResolutionIntent.HasValue ? (short)record.ResolutionIntent.Value : null,
                record.ResolutionClarity.HasValue ? (short)record.ResolutionClarity.Value : null,
                record.CreatedAt,
                record.UpdatedAt,
            ]);
        }

        sql.AppendLine(string.Join(",\n", valueTuples));
        sql.AppendLine(
            """
            ON CONFLICT (client_id, organization_url, project_id, repository_id, thread_id) DO UPDATE SET
                pull_request_id        = EXCLUDED.pull_request_id,
                file_path              = EXCLUDED.file_path,
                change_excerpt         = EXCLUDED.change_excerpt,
                comment_history_digest = EXCLUDED.comment_history_digest,
                resolution_summary     = EXCLUDED.resolution_summary,
                embedding_vector       = EXCLUDED.embedding_vector,
                memory_source          = EXCLUDED.memory_source,
                resolution_intent      = EXCLUDED.resolution_intent,
                resolution_clarity     = EXCLUDED.resolution_clarity,
                updated_at             = EXCLUDED.updated_at
            """);

        return (sql.ToString(), parameters.Select(parameter => parameter!).ToArray());
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
            throw new ArgumentOutOfRangeException(
                nameof(minSimilarity),
                minSimilarity,
                "MinSimilarity must be between 0 and 1.");
        }
    }

    private static string EscapeLikePattern(string value)
    {
        return value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    }
}
