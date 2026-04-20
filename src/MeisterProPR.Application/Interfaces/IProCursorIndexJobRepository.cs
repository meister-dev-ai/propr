// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Persistence abstraction for durable ProCursor index jobs.
/// </summary>
public interface IProCursorIndexJobRepository
{
    /// <summary>Persists a newly created durable index job.</summary>
    Task AddAsync(ProCursorIndexJob job, CancellationToken ct = default);

    /// <summary>Returns one durable index job by identifier.</summary>
    Task<ProCursorIndexJob?> GetByIdAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Returns the next pending job available for worker pickup.</summary>
    Task<ProCursorIndexJob?> GetNextPendingAsync(CancellationToken ct = default);

    /// <summary>Returns the next pending job whose source is not in the excluded set.</summary>
    Task<ProCursorIndexJob?> GetNextPendingAsync(
        IReadOnlyCollection<Guid> excludedSourceIds,
        CancellationToken ct = default);

    /// <summary>Returns active jobs for one knowledge source.</summary>
    Task<IReadOnlyList<ProCursorIndexJob>> ListActiveAsync(Guid knowledgeSourceId, CancellationToken ct = default);

    /// <summary>Returns whether a non-terminal job already exists for the deduplication key.</summary>
    Task<bool> HasActiveJobAsync(Guid trackedBranchId, string dedupKey, CancellationToken ct = default);

    /// <summary>Persists state changes to an existing index job.</summary>
    Task UpdateAsync(ProCursorIndexJob job, CancellationToken ct = default);
}
