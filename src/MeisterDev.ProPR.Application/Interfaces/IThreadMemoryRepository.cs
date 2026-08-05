// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Persistence abstraction for thread memory records.
///     All vector data crosses this boundary as <c>float[]</c>; the implementation converts to the
///     provider-specific type (e.g. pgvector <c>Vector</c>) internally.
/// </summary>
public interface IThreadMemoryRepository
{
    /// <summary>
    ///     Creates or updates the embedding record for the given thread.
    ///     Matches on <c>(ClientId, RepositoryId, ThreadId)</c>.
    /// </summary>
    Task UpsertAsync(ThreadMemoryRecord record, CancellationToken ct = default);

    /// <summary>
    ///     Creates or updates a batch of records in a single operation.
    ///     Used for store-swap data migrations and historical backfill.
    /// </summary>
    Task BulkUpsertAsync(IEnumerable<ThreadMemoryRecord> records, CancellationToken ct = default);

    /// <summary>
    ///     Removes the record for the given thread.
    /// </summary>
    /// <returns><see langword="true" /> if a record was deleted; <see langword="false" /> (no-op) if none existed.</returns>
    Task<bool> RemoveByThreadAsync(Guid clientId, string repositoryId, string threadId, CancellationToken ct = default);

    /// <summary>
    ///     Removes the record with the given <paramref name="id" />, scoped to the owning client.
    ///     Idempotent — returns <see langword="false" /> if no matching record exists.
    /// </summary>
    Task<bool> RemoveByIdAsync(Guid id, Guid clientId, CancellationToken ct = default);

    /// <summary>
    ///     Returns a paginated list of thread memory records for the given client.
    ///     Optionally filters by <paramref name="search" /> against <c>FilePath</c>,
    ///     <c>RepositoryId</c>, or <c>ResolutionSummary</c> text.
    /// </summary>
    Task<PagedResult<ThreadMemoryRecord>> GetPagedAsync(
        Guid clientId,
        string? search,
        int page,
        int pageSize,
        MemorySource? source = null,
        string? repositoryId = null,
        int? pullRequestId = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns display digests for the given record ids, scoped to the owning client.
    ///     Ids that do not exist, or belong to another client, are simply absent from the result.
    /// </summary>
    /// <remarks>
    ///     Resolving records by identifier is a keyed lookup. Reaching them by paging the client's
    ///     corpus and filtering in memory costs a full scan and sort per page and materializes
    ///     embedding vectors that are never read.
    /// </remarks>
    Task<IReadOnlyList<ThreadMemoryDigestDto>> GetDigestsByIdsAsync(
        Guid clientId,
        IReadOnlyCollection<Guid> ids,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns up to <paramref name="limit" /> display digests for one pull request, newest first,
    ///     alongside the total number of matching records so a caller can report a count it did not fetch.
    /// </summary>
    Task<PagedResult<ThreadMemoryDigestDto>> GetDigestsForPullRequestAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        MemorySource source,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns up to <paramref name="topN" /> records with cosine similarity ≥ <paramref name="minSimilarity" />,
    ///     ordered descending by similarity. Only returns records belonging to <paramref name="clientId" />.
    /// </summary>
    Task<IReadOnlyList<ThreadMemoryMatchDto>> FindSimilarAsync(
        Guid clientId,
        float[] queryVector,
        int topN,
        float minSimilarity,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns up to <paramref name="topN" /> exact file-path matches within the same repository,
    ///     ordered by most recently updated. Used as a deterministic fallback when semantic retrieval
    ///     finds no matches above the similarity threshold.
    /// </summary>
    Task<IReadOnlyList<ThreadMemoryMatchDto>> FindByFilePathAsync(
        Guid clientId,
        string repositoryId,
        string filePath,
        int topN,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns up to <paramref name="topN" /> records from the same pull request with cosine similarity
    ///     ≥ <paramref name="minSimilarity" />, ordered descending by similarity.
    /// </summary>
    Task<IReadOnlyList<ThreadMemoryMatchDto>> FindSimilarInPullRequestAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        float[] queryVector,
        int topN,
        float minSimilarity,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns up to <paramref name="topN" /> exact file-path matches from the same pull request,
    ///     ordered by most recently updated.
    /// </summary>
    Task<IReadOnlyList<ThreadMemoryMatchDto>> FindByPullRequestFilePathAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        string filePath,
        int topN,
        CancellationToken ct = default);
}
