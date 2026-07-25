// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Entities;

namespace MeisterDev.ProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     No-op memory activity log for offline review execution.
/// </summary>
public sealed class NoOpMemoryActivityLog : IMemoryActivityLog
{
    public Task AppendAsync(MemoryActivityLogEntry entry, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task<PagedResult<MemoryActivityLogEntry>> QueryAsync(
        Guid clientId,
        MemoryActivityLogQuery query,
        CancellationToken ct = default)
    {
        return Task.FromResult(new PagedResult<MemoryActivityLogEntry>([], 0, query.Page, query.PageSize));
    }
}
