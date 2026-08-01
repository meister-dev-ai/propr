// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     No-op thread-memory repository for offline review execution.
/// </summary>
public sealed class NoOpThreadMemoryRepository : IThreadMemoryRepository
{
    public Task UpsertAsync(ThreadMemoryRecord record, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task BulkUpsertAsync(IEnumerable<ThreadMemoryRecord> records, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task<bool> RemoveByThreadAsync(Guid clientId, string repositoryId, long threadId, CancellationToken ct = default)
    {
        return Task.FromResult(false);
    }

    public Task<bool> RemoveByIdAsync(Guid id, Guid clientId, CancellationToken ct = default)
    {
        return Task.FromResult(false);
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
        return Task.FromResult(new PagedResult<ThreadMemoryRecord>([], 0, page, pageSize));
    }

    public Task<IReadOnlyList<ThreadMemoryDigestDto>> GetDigestsByIdsAsync(
        Guid clientId,
        IReadOnlyCollection<Guid> ids,
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ThreadMemoryDigestDto>>([]);
    }

    public Task<PagedResult<ThreadMemoryDigestDto>> GetDigestsForPullRequestAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        MemorySource source,
        int limit,
        CancellationToken ct = default)
    {
        return Task.FromResult(new PagedResult<ThreadMemoryDigestDto>([], 0, 1, limit));
    }

    public Task<IReadOnlyList<ThreadMemoryMatchDto>> FindSimilarAsync(
        Guid clientId,
        float[] queryVector,
        int topN,
        float minSimilarity,
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ThreadMemoryMatchDto>>([]);
    }

    public Task<IReadOnlyList<ThreadMemoryMatchDto>> FindByFilePathAsync(
        Guid clientId,
        string repositoryId,
        string filePath,
        int topN,
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ThreadMemoryMatchDto>>([]);
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
        return Task.FromResult<IReadOnlyList<ThreadMemoryMatchDto>>([]);
    }

    public Task<IReadOnlyList<ThreadMemoryMatchDto>> FindByPullRequestFilePathAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        string filePath,
        int topN,
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ThreadMemoryMatchDto>>([]);
    }
}
