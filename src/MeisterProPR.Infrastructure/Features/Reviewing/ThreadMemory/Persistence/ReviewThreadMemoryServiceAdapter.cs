// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.ThreadMemory.Ports;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Events;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Infrastructure.Features.Reviewing.ThreadMemory.Persistence;

/// <summary>
///     Adapts the legacy thread-memory service onto the Reviewing-owned boundary.
/// </summary>
public sealed class ReviewThreadMemoryServiceAdapter(IThreadMemoryService inner) : IReviewThreadMemoryService
{
    public Task HandleThreadResolvedAsync(ThreadResolvedDomainEvent evt, CancellationToken ct = default)
    {
        return inner.HandleThreadResolvedAsync(evt, ct);
    }

    public Task HandleThreadReopenedAsync(ThreadReopenedDomainEvent evt, CancellationToken ct = default)
    {
        return inner.HandleThreadReopenedAsync(evt, ct);
    }

    public Task RecordNoOpAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        int threadId,
        string? previousStatus,
        string currentStatus,
        string reason,
        CancellationToken ct = default)
    {
        return inner.RecordNoOpAsync(
            clientId,
            repositoryId,
            pullRequestId,
            threadId,
            previousStatus,
            currentStatus,
            reason,
            ct);
    }

    public Task<ReviewResult> RetrieveAndReconsiderAsync(
        Guid clientId,
        ReviewJob job,
        string filePath,
        string? changeExcerpt,
        ReviewResult draftResult,
        Guid? protocolId,
        CancellationToken ct = default)
    {
        return inner.RetrieveAndReconsiderAsync(clientId, job, filePath, changeExcerpt, draftResult, protocolId, ct);
    }

    public Task<ThreadMemoryRecord> DismissFindingAsync(
        Guid clientId,
        string? filePath,
        string findingMessage,
        string? label,
        CancellationToken ct = default)
    {
        return inner.DismissFindingAsync(clientId, filePath, findingMessage, label, ct);
    }

    public Task<HistoricalDuplicateSuppressionMatchDto> FindDuplicateSuppressionMatchAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        string? filePath,
        string findingMessage,
        CancellationToken ct = default)
    {
        return inner.FindDuplicateSuppressionMatchAsync(
            clientId,
            repositoryId,
            pullRequestId,
            filePath,
            findingMessage,
            ct);
    }
}
