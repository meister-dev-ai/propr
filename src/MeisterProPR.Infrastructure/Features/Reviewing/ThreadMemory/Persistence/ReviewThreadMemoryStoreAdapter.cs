// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.ThreadMemory.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Features.Reviewing.ThreadMemory.Persistence;

/// <summary>
///     Adapts the legacy thread-memory repository onto the Reviewing-owned boundary.
/// </summary>
public sealed class ReviewThreadMemoryStoreAdapter(IThreadMemoryRepository inner) : IReviewThreadMemoryStore
{
    public Task UpsertAsync(ThreadMemoryRecord record, CancellationToken ct = default) => inner.UpsertAsync(record, ct);

    public Task BulkUpsertAsync(IEnumerable<ThreadMemoryRecord> records, CancellationToken ct = default) => inner.BulkUpsertAsync(records, ct);

    public Task<bool> RemoveByThreadAsync(Guid clientId, string repositoryId, int threadId, CancellationToken ct = default)
        => inner.RemoveByThreadAsync(clientId, repositoryId, threadId, ct);

    public Task<bool> RemoveByIdAsync(Guid id, Guid clientId, CancellationToken ct = default)
        => inner.RemoveByIdAsync(id, clientId, ct);

    public Task<PagedResult<ThreadMemoryRecord>> GetPagedAsync(Guid clientId, string? search, int page, int pageSize, MemorySource? source = null, string? repositoryId = null, int? pullRequestId = null, CancellationToken ct = default)
        => inner.GetPagedAsync(clientId, search, page, pageSize, source, repositoryId, pullRequestId, ct);

    public Task<IReadOnlyList<ThreadMemoryMatchDto>> FindSimilarAsync(Guid clientId, float[] queryVector, int topN, float minSimilarity, CancellationToken ct = default)
        => inner.FindSimilarAsync(clientId, queryVector, topN, minSimilarity, ct);

    public Task<IReadOnlyList<ThreadMemoryMatchDto>> FindByFilePathAsync(Guid clientId, string repositoryId, string filePath, int topN, CancellationToken ct = default)
        => inner.FindByFilePathAsync(clientId, repositoryId, filePath, topN, ct);

    public Task<IReadOnlyList<ThreadMemoryMatchDto>> FindSimilarInPullRequestAsync(Guid clientId, string repositoryId, int pullRequestId, float[] queryVector, int topN, float minSimilarity, CancellationToken ct = default)
        => inner.FindSimilarInPullRequestAsync(clientId, repositoryId, pullRequestId, queryVector, topN, minSimilarity, ct);

    public Task<IReadOnlyList<ThreadMemoryMatchDto>> FindByPullRequestFilePathAsync(Guid clientId, string repositoryId, int pullRequestId, string filePath, int topN, CancellationToken ct = default)
        => inner.FindByPullRequestFilePathAsync(clientId, repositoryId, pullRequestId, filePath, topN, ct);
}
