// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.ThreadMemory.Ports;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.ThreadMemory.Persistence;

/// <summary>
///     Adapts the legacy thread-memory repository onto the Reviewing-owned boundary.
/// </summary>
public sealed class ReviewThreadMemoryStoreAdapter(IThreadMemoryRepository inner) : IReviewThreadMemoryStore
{
    public Task UpsertAsync(ThreadMemoryRecord record, CancellationToken ct = default)
    {
        return inner.UpsertAsync(record, ct);
    }

    public Task BulkUpsertAsync(IEnumerable<ThreadMemoryRecord> records, CancellationToken ct = default)
    {
        return inner.BulkUpsertAsync(records, ct);
    }

    public Task<bool> RemoveByThreadAsync(
        Guid clientId,
        string repositoryId,
        string threadId,
        CancellationToken ct = default)
    {
        return inner.RemoveByThreadAsync(clientId, repositoryId, threadId, ct);
    }

    public Task<bool> RemoveByIdAsync(Guid id, Guid clientId, CancellationToken ct = default)
    {
        return inner.RemoveByIdAsync(id, clientId, ct);
    }

    public Task<PagedResult<ThreadMemoryRecord>> GetPagedAsync(
        Guid clientId,
        string? search,
        int page,
        int pageSize,
        MemorySource? source = null,
        string? repositoryId = null,
        int? pullRequestId = null,
        CancellationToken ct = default)
    {
        return inner.GetPagedAsync(clientId, search, page, pageSize, source, repositoryId, pullRequestId, ct);
    }

    public Task<IReadOnlyList<ThreadMemoryDigestDto>> GetDigestsByIdsAsync(
        Guid clientId,
        IReadOnlyCollection<Guid> ids,
        CancellationToken ct = default)
    {
        return inner.GetDigestsByIdsAsync(clientId, ids, ct);
    }

    public Task<PagedResult<ThreadMemoryDigestDto>> GetDigestsForPullRequestAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        MemorySource source,
        int limit,
        CancellationToken ct = default)
    {
        return inner.GetDigestsForPullRequestAsync(clientId, repositoryId, pullRequestId, source, limit, ct);
    }

    public Task<IReadOnlyList<ThreadMemoryMatchDto>> FindSimilarAsync(
        Guid clientId,
        float[] queryVector,
        int topN,
        float minSimilarity,
        CancellationToken ct = default)
    {
        return inner.FindSimilarAsync(clientId, queryVector, topN, minSimilarity, ct);
    }

    public Task<IReadOnlyList<ThreadMemoryMatchDto>> FindByFilePathAsync(
        Guid clientId,
        string repositoryId,
        string filePath,
        int topN,
        CancellationToken ct = default)
    {
        return inner.FindByFilePathAsync(clientId, repositoryId, filePath, topN, ct);
    }

    public Task<IReadOnlyList<ThreadMemoryMatchDto>> FindSimilarInPullRequestAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        float[] queryVector,
        int topN,
        float minSimilarity,
        CancellationToken ct = default)
    {
        return inner.FindSimilarInPullRequestAsync(
            clientId,
            repositoryId,
            pullRequestId,
            queryVector,
            topN,
            minSimilarity,
            ct);
    }

    public Task<IReadOnlyList<ThreadMemoryMatchDto>> FindByPullRequestFilePathAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        string filePath,
        int topN,
        CancellationToken ct = default)
    {
        return inner.FindByPullRequestFilePathAsync(clientId, repositoryId, pullRequestId, filePath, topN, ct);
    }
}
