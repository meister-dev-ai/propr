// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Write and query interface for the append-only <c>memory_activity_log</c> table.
///     Records every crawl-side thread lifecycle state machine evaluation — including no-ops.
/// </summary>
public interface IMemoryActivityLog
{
    /// <summary>
    ///     Appends a new entry to the log.  Never updates or deletes existing entries.
    ///     Never throws — on any failure, logs a warning and returns.
    /// </summary>
    Task AppendAsync(MemoryActivityLogEntry entry, CancellationToken ct = default);

    /// <summary>
    ///     Returns a paginated list of log entries for the given client, filtered by the query parameters.
    /// </summary>
    Task<PagedResult<MemoryActivityLogEntry>> QueryAsync(
        Guid clientId,
        MemoryActivityLogQuery query,
        CancellationToken ct = default);
}
