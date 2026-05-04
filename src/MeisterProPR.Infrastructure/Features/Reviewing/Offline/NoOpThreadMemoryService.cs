// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Events;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     No-op thread-memory service for offline review execution.
/// </summary>
public sealed class NoOpThreadMemoryService : IThreadMemoryService
{
    public Task HandleThreadResolvedAsync(ThreadResolvedDomainEvent evt, CancellationToken ct = default) => Task.CompletedTask;

    public Task HandleThreadReopenedAsync(ThreadReopenedDomainEvent evt, CancellationToken ct = default) => Task.CompletedTask;

    public Task RecordNoOpAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        int threadId,
        string? previousStatus,
        string currentStatus,
        string reason,
        CancellationToken ct = default) => Task.CompletedTask;

    public Task<ReviewResult> RetrieveAndReconsiderAsync(
        Guid clientId,
        ReviewJob job,
        string filePath,
        string? changeExcerpt,
        ReviewResult draftResult,
        Guid? protocolId,
        CancellationToken ct = default,
        float? temperature = null) => Task.FromResult(draftResult);

    public Task<ThreadMemoryRecord> DismissFindingAsync(
        Guid clientId,
        string? filePath,
        string findingMessage,
        string? label,
        CancellationToken ct = default)
    {
        return Task.FromResult(new ThreadMemoryRecord
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            ThreadId = 1,
            RepositoryId = string.Empty,
            PullRequestId = 1,
            FilePath = filePath,
            CommentHistoryDigest = label ?? findingMessage,
            ResolutionSummary = findingMessage,
            EmbeddingVector = [1f],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
    }

    public Task<HistoricalDuplicateSuppressionMatchDto> FindDuplicateSuppressionMatchAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        string? filePath,
        string findingMessage,
        CancellationToken ct = default)
    {
        return Task.FromResult(HistoricalDuplicateSuppressionMatchDto.NoMatch());
    }
}
