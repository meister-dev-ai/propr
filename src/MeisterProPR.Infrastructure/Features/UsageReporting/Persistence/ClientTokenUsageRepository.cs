// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

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
        CancellationToken ct)
    {
        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true)
        {
            // PostgreSQL: atomic upsert via ON CONFLICT DO UPDATE — no read-modify-write race.
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO client_token_usage_samples (id, client_id, model_id, date, input_tokens, output_tokens)
                VALUES (gen_random_uuid(), {0}, {1}, {2}, {3}, {4})
                ON CONFLICT (client_id, model_id, date)
                DO UPDATE SET
                    input_tokens  = client_token_usage_samples.input_tokens  + EXCLUDED.input_tokens,
                    output_tokens = client_token_usage_samples.output_tokens + EXCLUDED.output_tokens
                """,
                clientId, modelId, date, inputTokens, outputTokens);
        }
        else
        {
            // InMemory fallback: read-modify-write (acceptable for tests only).
            var existing = await db.ClientTokenUsageSamples
                .FirstOrDefaultAsync(
                    s => s.ClientId == clientId && s.ModelId == modelId && s.Date == date,
                    ct);

            if (existing is null)
            {
                db.ClientTokenUsageSamples.Add(new ClientTokenUsageSample
                {
                    Id = Guid.NewGuid(),
                    ClientId = clientId,
                    ModelId = modelId,
                    Date = date,
                    InputTokens = inputTokens,
                    OutputTokens = outputTokens,
                });
            }
            else
            {
                existing.InputTokens += inputTokens;
                existing.OutputTokens += outputTokens;
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
}
