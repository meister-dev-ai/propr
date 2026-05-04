// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     No-op thread-memory repository for offline review execution.
/// </summary>
public sealed class NoOpThreadMemoryRepository : IThreadMemoryRepository
{
    public Task UpsertAsync(ThreadMemoryRecord record, CancellationToken ct = default) => Task.CompletedTask;

    public Task BulkUpsertAsync(IEnumerable<ThreadMemoryRecord> records, CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> RemoveByThreadAsync(Guid clientId, string repositoryId, int threadId, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<bool> RemoveByIdAsync(Guid id, Guid clientId, CancellationToken ct = default)
        => Task.FromResult(false);

    public Task<PagedResult<ThreadMemoryRecord>> GetPagedAsync(
        Guid clientId,
        string? search,
        int page,
        int pageSize,
        MemorySource? source = null,
        string? repositoryId = null,
        int? pullRequestId = null,
        CancellationToken ct = default)
        => Task.FromResult(new PagedResult<ThreadMemoryRecord>([], 0, page, pageSize));

    public Task<IReadOnlyList<ThreadMemoryMatchDto>> FindSimilarAsync(
        Guid clientId,
        float[] queryVector,
        int topN,
        float minSimilarity,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ThreadMemoryMatchDto>>([]);

    public Task<IReadOnlyList<ThreadMemoryMatchDto>> FindByFilePathAsync(
        Guid clientId,
        string repositoryId,
        string filePath,
        int topN,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ThreadMemoryMatchDto>>([]);

    public Task<IReadOnlyList<ThreadMemoryMatchDto>> FindSimilarInPullRequestAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        float[] queryVector,
        int topN,
        float minSimilarity,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ThreadMemoryMatchDto>>([]);

    public Task<IReadOnlyList<ThreadMemoryMatchDto>> FindByPullRequestFilePathAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        string filePath,
        int topN,
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ThreadMemoryMatchDto>>([]);
}
