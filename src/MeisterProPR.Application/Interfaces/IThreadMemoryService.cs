// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Domain.Events;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Orchestrates thread memory lifecycle and memory-augmented reconsideration.
///     All methods must never throw to the caller — failures are recorded as protocol events and swallowed.
/// </summary>
public interface IThreadMemoryService
{
    /// <summary>
    ///     Handles a <see cref="ThreadResolvedDomainEvent" /> raised by the crawl-side state machine.
    ///     Generates an embedding for the resolved thread and upserts it into the client's memory store.
    ///     Appends a <c>MemoryActivityLogEntry (Stored)</c> on success or <c>(Stored, reason=error)</c>
    ///     on failure. Never throws.
    /// </summary>
    Task HandleThreadResolvedAsync(ThreadResolvedDomainEvent evt, CancellationToken ct = default);

    /// <summary>
    ///     Handles a <see cref="ThreadReopenedDomainEvent" /> raised by the crawl-side state machine.
    ///     Removes the stored embedding for the thread from the client's memory store.
    ///     Appends a <c>MemoryActivityLogEntry (Removed)</c>. Never throws.
    /// </summary>
    Task HandleThreadReopenedAsync(ThreadReopenedDomainEvent evt, CancellationToken ct = default);

    /// <summary>
    ///     Records a no-op evaluation result in the <c>memory_activity_log</c>.
    ///     Called when the thread status did not transition (already resolved, still active, etc.).
    ///     Never throws.
    /// </summary>
    Task RecordNoOpAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        int threadId,
        string? previousStatus,
        string currentStatus,
        string reason,
        CancellationToken ct = default);

    /// <summary>
    ///     Queries the client's embedding store for past resolutions similar to the current draft findings,
    ///     then asks the AI to reconsider those findings in light of the matches.
    ///     Returns the reconsidered result, or <paramref name="draftResult" /> unchanged if no matches are
    ///     found, the store is unavailable, or any other failure occurs. Never throws.
    /// </summary>
    Task<ReviewResult> RetrieveAndReconsiderAsync(
        Guid clientId,
        ReviewJob job,
        string filePath,
        string? changeExcerpt,
        ReviewResult draftResult,
        Guid? protocolId,
        CancellationToken ct = default);

    /// <summary>
    ///     Stores a finding dismissal as an <c>AdminDismissed</c> memory record, generating an embedding
    ///     for the finding message so the memory reconsideration pipeline can suppress similar future findings.
    ///     Returns the upserted <see cref="ThreadMemoryRecord" />.
    /// </summary>
    Task<ThreadMemoryRecord> DismissFindingAsync(
        Guid clientId,
        string? filePath,
        string findingMessage,
        string? label,
        CancellationToken ct = default);

    /// <summary>
    ///     Queries pull-request-scoped historical thread memory to determine whether the current finding
    ///     is a duplicate of a previously resolved or dismissed concern. Never throws.
    /// </summary>
    Task<HistoricalDuplicateSuppressionMatchDto> FindDuplicateSuppressionMatchAsync(
        Guid clientId,
        string repositoryId,
        int pullRequestId,
        string? filePath,
        string findingMessage,
        CancellationToken ct = default);
}
