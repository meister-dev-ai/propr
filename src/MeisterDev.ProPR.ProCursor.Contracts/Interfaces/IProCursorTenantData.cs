// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs.ProCursor;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Stops and deletes the ProCursor data of knowledge sources and clients whose tenant is being deleted. The data
///     is stored in the ProCursor database, which can be separate from the ProPR database.
/// </summary>
public interface IProCursorTenantData
{
    /// <summary>Cancels the pending index jobs of the knowledge sources and reports the index jobs that still run.</summary>
    /// <param name="knowledgeSourceIds">The knowledge sources whose indexing stops.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ProCursorIndexingStop> StopIndexingAsync(
        IReadOnlyCollection<Guid> knowledgeSourceIds,
        CancellationToken ct = default);

    /// <summary>
    ///     Deletes the index jobs and snapshots of the knowledge sources with their chunks and symbols, and the token
    ///     usage of the knowledge sources and clients.
    /// </summary>
    /// <param name="knowledgeSourceIds">The knowledge sources whose data is deleted.</param>
    /// <param name="clientIds">The clients whose token usage is deleted.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The deleted rows, by table.</returns>
    Task<IReadOnlyDictionary<string, int>> DeleteAsync(
        IReadOnlyCollection<Guid> knowledgeSourceIds,
        IReadOnlyCollection<Guid> clientIds,
        CancellationToken ct = default);
}
