// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IMemoryActivityLog" />.</summary>
public sealed partial class MemoryActivityLogRepository(
    MeisterProPRDbContext dbContext,
    ILogger<MemoryActivityLogRepository> logger) : IMemoryActivityLog
{
    /// <inheritdoc />
    public async Task AppendAsync(MemoryActivityLogEntry entry, CancellationToken ct = default)
    {
        try
        {
            await dbContext.MemoryActivityLogEntries.AddAsync(entry, ct);
            await dbContext.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            LogAppendFailed(logger, entry.ThreadId, entry.ClientId, ex);
        }
    }

    /// <inheritdoc />
    public async Task<PagedResult<MemoryActivityLogEntry>> QueryAsync(
        Guid clientId,
        MemoryActivityLogQuery query,
        CancellationToken ct = default)
    {
        var q = dbContext.MemoryActivityLogEntries
            .AsNoTracking()
            .Where(e => e.ClientId == clientId);

        if (query.ThreadId.HasValue)
        {
            q = q.Where(e => e.ThreadId == query.ThreadId.Value);
        }

        if (query.PullRequestId.HasValue)
        {
            q = q.Where(e => e.PullRequestId == query.PullRequestId.Value);
        }

        if (query.RepositoryId is not null)
        {
            q = q.Where(e => e.RepositoryId == query.RepositoryId);
        }

        if (query.Action.HasValue)
        {
            q = q.Where(e => e.Action == query.Action.Value);
        }

        if (query.FromDate.HasValue)
        {
            q = q.Where(e => e.OccurredAt >= query.FromDate.Value);
        }

        if (query.ToDate.HasValue)
        {
            q = q.Where(e => e.OccurredAt <= query.ToDate.Value);
        }

        var totalCount = await q.CountAsync(ct);

        var items = await q
            .OrderByDescending(e => e.OccurredAt)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(ct);

        return new PagedResult<MemoryActivityLogEntry>(items, totalCount, query.Page, query.PageSize);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to append MemoryActivityLogEntry for thread {ThreadId} / client {ClientId}")]
    private static partial void LogAppendFailed(ILogger logger, int threadId, Guid clientId, Exception ex);
}
