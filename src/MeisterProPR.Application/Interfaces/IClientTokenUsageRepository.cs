// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Persistence contract for daily token usage aggregates per client and AI model.
/// </summary>
public interface IClientTokenUsageRepository
{
    /// <summary>
    ///     Atomically increments the token counts for the given (clientId, modelId, date) aggregate row.
    ///     Creates the row if it does not yet exist.
    /// </summary>
    Task UpsertAsync(
        Guid clientId,
        string modelId,
        DateOnly date,
        long inputTokens,
        long outputTokens,
        CancellationToken ct);

    /// <summary>
    ///     Returns all samples for <paramref name="clientId" /> whose date falls within the
    ///     inclusive [<paramref name="from" />, <paramref name="to" />] range, ordered by date ascending.
    /// </summary>
    Task<IReadOnlyList<ClientTokenUsageSample>> GetByClientAndDateRangeAsync(
        Guid clientId,
        DateOnly from,
        DateOnly to,
        CancellationToken ct);
}
