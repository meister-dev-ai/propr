// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;
using MeisterProPR.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace MeisterProPR.Infrastructure.Repositories;

/// <summary>EF Core implementation of <see cref="IBlockedPullRequestStore" />.</summary>
public sealed partial class BlockedPullRequestStore(
    MeisterProPRDbContext dbContext,
    ILogger<BlockedPullRequestStore> logger) : IBlockedPullRequestStore
{
    /// <inheritdoc />
    public async Task<bool> IsBlockedAsync(
        Guid clientId,
        string providerScopePath,
        string providerProjectKey,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct = default)
    {
        try
        {
            return await dbContext.BlockedPullRequests
                .AsNoTracking()
                .AnyAsync(
                    b => b.ClientId == clientId
                         && b.ProviderScopePath == providerScopePath
                         && b.ProviderProjectKey == providerProjectKey
                         && b.RepositoryId == repositoryId
                         && b.PullRequestId == pullRequestId,
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Fail open: a block is an administrator convenience, so an inability to determine block state
            // (a transient database error, or a not-yet-applied migration) must never halt review intake.
            // Treat the pull request as not blocked and let processing proceed.
            LogBlockCheckFailed(logger, clientId, pullRequestId, exception);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> BlockAsync(
        Guid clientId,
        string providerScopePath,
        string providerProjectKey,
        string repositoryId,
        int pullRequestId,
        Guid blockedByUserId,
        string? reason,
        CancellationToken ct = default)
    {
        var alreadyBlocked = await dbContext.BlockedPullRequests
            .AnyAsync(
                b => b.ClientId == clientId
                     && b.ProviderScopePath == providerScopePath
                     && b.ProviderProjectKey == providerProjectKey
                     && b.RepositoryId == repositoryId
                     && b.PullRequestId == pullRequestId,
                ct)
            .ConfigureAwait(false);
        if (alreadyBlocked)
        {
            return false;
        }

        var block = new BlockedPullRequest(
            Guid.NewGuid(),
            clientId,
            providerScopePath,
            providerProjectKey,
            repositoryId,
            pullRequestId,
            blockedByUserId,
            reason);

        dbContext.BlockedPullRequests.Add(block);
        try
        {
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Lost a race against a concurrent block for the same pull request; the unique index held.
            // Only a unique-violation is a benign duplicate — any other write failure must surface.
            dbContext.Entry(block).State = EntityState.Detached;
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> UnblockAsync(
        Guid clientId,
        string providerScopePath,
        string providerProjectKey,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct = default)
    {
        var block = await dbContext.BlockedPullRequests
            .FirstOrDefaultAsync(
                b => b.ClientId == clientId
                     && b.ProviderScopePath == providerScopePath
                     && b.ProviderProjectKey == providerProjectKey
                     && b.RepositoryId == repositoryId
                     && b.PullRequestId == pullRequestId,
                ct)
            .ConfigureAwait(false);
        if (block is null)
        {
            return false;
        }

        dbContext.BlockedPullRequests.Remove(block);
        await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<BlockedPullRequest>> ListForClientAsync(Guid clientId, CancellationToken ct = default)
    {
        return await dbContext.BlockedPullRequests
            .AsNoTracking()
            .Where(b => b.ClientId == clientId)
            .OrderByDescending(b => b.BlockedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Failed to determine block state for client {ClientId} PR #{PrId}; treating it as not blocked so review intake can proceed.")]
    private static partial void LogBlockCheckFailed(ILogger logger, Guid clientId, int prId, Exception exception);
}
