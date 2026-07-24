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
    ///     Atomically increments the token counts for the given (clientId, modelId, logicalModelName, date) aggregate
    ///     row. Creates the row if it does not yet exist. Pass the empty string for <paramref name="logicalModelName" />
    ///     when the usage is not attributable to a logical model (raw model / non-logical-model call).
    /// </summary>
    Task UpsertAsync(
        Guid clientId,
        string modelId,
        DateOnly date,
        long inputTokens,
        long outputTokens,
        CancellationToken ct,
        long cachedInputTokens = 0,
        long cacheWriteTokens = 0,
        long reasoningTokens = 0,
        decimal? estimatedCostUsd = null,
        string logicalModelName = "");

    /// <summary>
    ///     Returns all samples for <paramref name="clientId" /> whose date falls within the
    ///     inclusive [<paramref name="from" />, <paramref name="to" />] range, ordered by date ascending.
    /// </summary>
    Task<IReadOnlyList<ClientTokenUsageSample>> GetByClientAndDateRangeAsync(
        Guid clientId,
        DateOnly from,
        DateOnly to,
        CancellationToken ct);

    /// <summary>
    ///     Returns the total tokens (input + output) each client accumulated within the inclusive
    ///     [<paramref name="from" />, <paramref name="to" />] date range, keyed by client id. Clients
    ///     with no samples in the range are absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, long>> GetRecentTotalsByClientAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken ct);

    /// <summary>
    ///     Returns the estimated USD cost each client accumulated within the inclusive
    ///     [<paramref name="from" />, <paramref name="to" />] date range, keyed by client id. Unpriced usage is
    ///     omitted from the sum (not coerced to zero). Clients with no samples in the range are absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, decimal>> GetCostByClientAndDateRangeAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken ct);

    /// <summary>
    ///     Returns the estimated USD cost aggregated per calendar month across the given <paramref name="clientIds" />
    ///     within the inclusive [<paramref name="from" />, <paramref name="to" />] date range, keyed by (year, month).
    ///     Unpriced usage is omitted from the sum. Months with no samples are absent from the result.
    /// </summary>
    Task<IReadOnlyDictionary<(int Year, int Month), decimal>> GetMonthlyCostForClientsAsync(
        IReadOnlyCollection<Guid> clientIds,
        DateOnly from,
        DateOnly to,
        CancellationToken ct);
}
