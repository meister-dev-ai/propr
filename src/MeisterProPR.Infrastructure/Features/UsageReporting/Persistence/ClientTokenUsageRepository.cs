// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>
///     EF Core / PostgreSQL implementation of <see cref="IClientTokenUsageRepository" />.
///     Uses a raw SQL upsert (<c>INSERT … ON CONFLICT DO UPDATE</c>) so that concurrent
///     protocol-recorder calls accumulate safely without read-modify-write races.
///     Falls back to an EF Core LINQ upsert when the active provider is not Npgsql
///     (e.g. the InMemory provider used in lightweight unit/integration tests).
/// </summary>
public sealed class ClientTokenUsageRepository(MeisterProPRDbContext db) : IClientTokenUsageRepository
{
    /// <inheritdoc />
    public async Task UpsertAsync(
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
        string logicalModelName = "")
    {
        // The aggregate key is non-null in logical_model_name (empty for raw usage) so ON CONFLICT matches every row.
        logicalModelName ??= string.Empty;

        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
        {
            // PostgreSQL: atomic upsert via ON CONFLICT DO UPDATE — no read-modify-write race.
            // The nullable cost accumulates only when at least one side is priced; when both the
            // stored value and the incoming delta are null (unpriced model) the sample stays null,
            // so "pricing unknown" is never silently collapsed into a real zero.
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO client_token_usage_samples
                    (id, client_id, model_id, logical_model_name, date, input_tokens, output_tokens, cached_input_tokens, cache_write_tokens, reasoning_tokens, estimated_cost_usd)
                VALUES (gen_random_uuid(), {0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, @estimated_cost_usd)
                ON CONFLICT (client_id, model_id, logical_model_name, date)
                DO UPDATE SET
                    input_tokens        = client_token_usage_samples.input_tokens        + EXCLUDED.input_tokens,
                    output_tokens       = client_token_usage_samples.output_tokens       + EXCLUDED.output_tokens,
                    cached_input_tokens = client_token_usage_samples.cached_input_tokens + EXCLUDED.cached_input_tokens,
                    cache_write_tokens  = client_token_usage_samples.cache_write_tokens  + EXCLUDED.cache_write_tokens,
                    reasoning_tokens    = client_token_usage_samples.reasoning_tokens    + EXCLUDED.reasoning_tokens,
                    estimated_cost_usd  = CASE
                                              WHEN client_token_usage_samples.estimated_cost_usd IS NULL AND EXCLUDED.estimated_cost_usd IS NULL THEN NULL
                                              ELSE COALESCE(client_token_usage_samples.estimated_cost_usd, 0) + COALESCE(EXCLUDED.estimated_cost_usd, 0)
                                          END
                """,
                clientId,
                modelId,
                logicalModelName,
                date,
                inputTokens,
                outputTokens,
                cachedInputTokens,
                cacheWriteTokens,
                reasoningTokens,
                new NpgsqlParameter("estimated_cost_usd", NpgsqlDbType.Numeric)
                {
                    Value = (object?)estimatedCostUsd ?? DBNull.Value,
                });
        }
        else
        {
            // InMemory fallback: read-modify-write (acceptable for tests only).
            var existing = await db.ClientTokenUsageSamples
                .FirstOrDefaultAsync(
                    s => s.ClientId == clientId && s.ModelId == modelId && s.LogicalModelName == logicalModelName && s.Date == date,
                    ct);

            if (existing is null)
            {
                db.ClientTokenUsageSamples.Add(
                    new ClientTokenUsageSample
                    {
                        Id = Guid.NewGuid(),
                        ClientId = clientId,
                        ModelId = modelId,
                        LogicalModelName = logicalModelName,
                        Date = date,
                        InputTokens = inputTokens,
                        OutputTokens = outputTokens,
                        CachedInputTokens = cachedInputTokens,
                        CacheWriteTokens = cacheWriteTokens,
                        ReasoningTokens = reasoningTokens,
                        EstimatedCostUsd = estimatedCostUsd,
                    });
            }
            else
            {
                existing.InputTokens += inputTokens;
                existing.OutputTokens += outputTokens;
                existing.CachedInputTokens += cachedInputTokens;
                existing.CacheWriteTokens += cacheWriteTokens;
                existing.ReasoningTokens += reasoningTokens;
                existing.EstimatedCostUsd = existing.EstimatedCostUsd is null && estimatedCostUsd is null
                    ? null
                    : (existing.EstimatedCostUsd ?? 0m) + (estimatedCostUsd ?? 0m);
            }

            await db.SaveChangesAsync(ct);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ClientTokenUsageSample>> GetByClientAndDateRangeAsync(
        Guid clientId,
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        return await db.ClientTokenUsageSamples
            .Where(s => s.ClientId == clientId && s.Date >= from && s.Date <= to)
            .OrderBy(s => s.Date)
            .ThenBy(s => s.ModelId)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, long>> GetRecentTotalsByClientAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        var totals = await db.ClientTokenUsageSamples
            .Where(s => s.Date >= from && s.Date <= to)
            .GroupBy(s => s.ClientId)
            .Select(g => new { ClientId = g.Key, Total = g.Sum(s => s.InputTokens + s.OutputTokens) })
            .ToListAsync(ct);

        return totals.ToDictionary(t => t.ClientId, t => t.Total);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, decimal>> GetCostByClientAndDateRangeAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        // SQL SUM ignores NULLs, so unpriced samples are omitted from the total rather than counted as zero.
        var totals = await db.ClientTokenUsageSamples
            .Where(s => s.Date >= from && s.Date <= to)
            .GroupBy(s => s.ClientId)
            .Select(g => new { ClientId = g.Key, Cost = g.Sum(s => s.EstimatedCostUsd) })
            .ToListAsync(ct);

        return totals.ToDictionary(t => t.ClientId, t => t.Cost ?? 0m);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<(int Year, int Month), decimal>> GetMonthlyCostForClientsAsync(
        IReadOnlyCollection<Guid> clientIds,
        DateOnly from,
        DateOnly to,
        CancellationToken ct)
    {
        if (clientIds.Count == 0)
        {
            return new Dictionary<(int, int), decimal>();
        }

        var rows = await db.ClientTokenUsageSamples
            .Where(s => clientIds.Contains(s.ClientId) && s.Date >= from && s.Date <= to)
            .GroupBy(s => new { s.Date.Year, s.Date.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, Cost = g.Sum(s => s.EstimatedCostUsd) })
            .ToListAsync(ct);

        return rows.ToDictionary(r => (r.Year, r.Month), r => r.Cost ?? 0m);
    }
}
