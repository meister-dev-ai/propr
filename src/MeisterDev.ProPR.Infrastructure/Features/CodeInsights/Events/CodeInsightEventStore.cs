// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.ProPR.Application.Features.CodeInsights.Events;
using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MeisterDev.ProPR.Infrastructure.Features.CodeInsights.Events;

/// <summary>
///     Stores the quality-condition transitions. Append-only: a transition is a historical fact, and rewriting
///     one would change what an alert was raised about after the fact.
/// </summary>
public sealed class CodeInsightEventStore(
    MeisterProPRDbContext dbContext,
    IDbContextFactory<MeisterProPRDbContext>? contextFactory = null) : ICodeInsightEventStore
{
    public Task<CodeInsightConditionState?> GetCurrentStateAsync(
        CodeInsightEventScope scope,
        CodeInsightEventType eventType,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return this.WithDbAsync(
            async db =>
            {
                var latest = await db.CodeInsightEvents
                    .AsNoTracking()
                    .Where(evt => evt.ClientId == scope.ClientId
                                  && evt.RepositoryId == scope.RepositoryId
                                  && evt.FilePath == scope.FilePath
                                  && evt.EventType == eventType)
                    .OrderByDescending(evt => evt.OccurredAt)
                    // Two transitions can share an instant in a fast test or a clock with coarse resolution; the
                    // v7 identifier is time-ordered, so it breaks the tie the same way the clock would have.
                    .ThenByDescending(evt => evt.Id)
                    .Select(evt => (CodeInsightConditionState?)evt.State)
                    .FirstOrDefaultAsync(ct);

                return latest;
            },
            ct);
    }

    public Task AppendAsync(CodeInsightEvent transition, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transition);

        return this.WithDbAsync<object?>(
            async db =>
            {
                db.CodeInsightEvents.Add(transition);
                await db.SaveChangesAsync(ct);
                return null;
            },
            ct);
    }

    public Task<IReadOnlyList<CodeInsightEvent>> GetByClientSinceAsync(
        Guid clientId,
        DateTimeOffset since,
        CancellationToken ct = default)
    {
        return this.WithDbAsync<IReadOnlyList<CodeInsightEvent>>(
            async db => await db.CodeInsightEvents
                .AsNoTracking()
                .Where(evt => evt.ClientId == clientId && evt.OccurredAt >= since)
                .OrderBy(evt => evt.OccurredAt)
                .ThenBy(evt => evt.Id)
                .ToListAsync(ct),
            ct);
    }

    private async Task<T> WithDbAsync<T>(Func<MeisterProPRDbContext, Task<T>> operation, CancellationToken ct)
    {
        if (contextFactory is null)
        {
            return await operation(dbContext);
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await operation(db);
    }
}
