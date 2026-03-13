using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>Database-backed client registry using PostgreSQL.</summary>
public sealed class PostgresClientRegistry(
    MeisterProPRDbContext dbContext,
    ILogger<PostgresClientRegistry> logger) : IClientRegistry
{
    private readonly ILogger<PostgresClientRegistry> _logger = logger;

    /// <inheritdoc />
    public bool IsValidKey(string clientKey)
    {
        if (string.IsNullOrWhiteSpace(clientKey))
        {
            this._logger.LogDebug($"Invalid client key: {clientKey}");
            return false;
        }

        return dbContext.Clients.Any(c => c.Key == clientKey && c.IsActive);
    }

    /// <inheritdoc />
    public async Task<Guid?> GetClientIdByKeyAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            this._logger.LogError($"Invalid client key: {key}");
            return null;
        }

        var client = await dbContext.Clients
            .Where(c => c.Key == key && c.IsActive)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);

        if (client == null)
        {
            this._logger.LogDebug($"Client not found for key: {key}");
        }

        return client;
    }

    /// <inheritdoc />
    public async Task<Guid?> GetReviewerIdAsync(Guid clientId, CancellationToken ct = default)
    {
        return await dbContext.Clients
            .Where(c => c.Id == clientId)
            .Select(c => c.ReviewerId)
            .FirstOrDefaultAsync(ct);
    }
}