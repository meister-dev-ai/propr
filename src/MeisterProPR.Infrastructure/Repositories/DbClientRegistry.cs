using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>Database-backed client registry using EF Core.</summary>
public sealed partial class DbClientRegistry(
    MeisterProPRDbContext dbContext,
    ILogger<DbClientRegistry> logger) : IClientRegistry
{
    /// <inheritdoc />
    public bool IsValidKey(string clientKey)
    {
        if (string.IsNullOrWhiteSpace(clientKey))
        {
            LogKeyNullOrWhitespace(logger);
            return false;
        }

        return dbContext.Clients.Any(c => c.Key == clientKey && c.IsActive);
    }

    /// <inheritdoc />
    public async Task<Guid?> GetClientIdByKeyAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            LogKeyNullOrWhitespace(logger);
            return null;
        }

        var client = await dbContext.Clients
            .Where(c => c.Key == key && c.IsActive)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);

        if (client == null)
        {
            LogClientNotFound(logger);
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

    /// <inheritdoc />
    public async Task<CommentResolutionBehavior> GetCommentResolutionBehaviorAsync(Guid clientId, CancellationToken ct = default)
    {
        return await dbContext.Clients
            .Where(c => c.Id == clientId)
            .Select(c => c.CommentResolutionBehavior)
            .FirstOrDefaultAsync(ct);
    }

    /// <inheritdoc />
    public async Task<string?> GetCustomSystemMessageAsync(Guid clientId, CancellationToken ct = default)
    {
        return await dbContext.Clients
            .Where(c => c.Id == clientId)
            .Select(c => c.CustomSystemMessage)
            .FirstOrDefaultAsync(ct);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Client registry: key is null or whitespace")]
    private static partial void LogKeyNullOrWhitespace(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Client registry: no active client found for key")]
    private static partial void LogClientNotFound(ILogger logger);
}
