// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     No-op memory activity log for offline review execution.
/// </summary>
public sealed class NoOpMemoryActivityLog : IMemoryActivityLog
{
    public Task AppendAsync(MemoryActivityLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;

    public Task<PagedResult<MemoryActivityLogEntry>> QueryAsync(
        Guid clientId,
        MemoryActivityLogQuery query,
        CancellationToken ct = default)
        => Task.FromResult(new PagedResult<MemoryActivityLogEntry>([], 0, query.Page, query.PageSize));
}
