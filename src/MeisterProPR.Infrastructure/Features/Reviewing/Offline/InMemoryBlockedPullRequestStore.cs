// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Concurrent;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     In-memory <see cref="IBlockedPullRequestStore" /> used by the offline (database-free) composition,
///     mirroring the offline review-job store. Registered as a singleton so blocks persist for the process
///     lifetime.
/// </summary>
public sealed class InMemoryBlockedPullRequestStore : IBlockedPullRequestStore
{
    private readonly ConcurrentDictionary<string, BlockedPullRequest> _blocks = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<bool> IsBlockedAsync(
        Guid clientId,
        string providerScopePath,
        string providerProjectKey,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct = default)
    {
        var key = BuildKey(clientId, providerScopePath, providerProjectKey, repositoryId, pullRequestId);
        return Task.FromResult(this._blocks.ContainsKey(key));
    }

    /// <inheritdoc />
    public Task<bool> BlockAsync(
        Guid clientId,
        string providerScopePath,
        string providerProjectKey,
        string repositoryId,
        int pullRequestId,
        Guid blockedByUserId,
        string? reason,
        CancellationToken ct = default)
    {
        var key = BuildKey(clientId, providerScopePath, providerProjectKey, repositoryId, pullRequestId);
        var block = new BlockedPullRequest(
            Guid.NewGuid(),
            clientId,
            providerScopePath,
            providerProjectKey,
            repositoryId,
            pullRequestId,
            blockedByUserId,
            reason);
        return Task.FromResult(this._blocks.TryAdd(key, block));
    }

    /// <inheritdoc />
    public Task<bool> UnblockAsync(
        Guid clientId,
        string providerScopePath,
        string providerProjectKey,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct = default)
    {
        var key = BuildKey(clientId, providerScopePath, providerProjectKey, repositoryId, pullRequestId);
        return Task.FromResult(this._blocks.TryRemove(key, out _));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<BlockedPullRequest>> ListForClientAsync(Guid clientId, CancellationToken ct = default)
    {
        var blocks = this._blocks.Values
            .Where(b => b.ClientId == clientId)
            .OrderByDescending(b => b.BlockedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<BlockedPullRequest>>(blocks);
    }

    private static string BuildKey(
        Guid clientId,
        string providerScopePath,
        string providerProjectKey,
        string repositoryId,
        int pullRequestId)
    {
        return string.Join('\n', clientId.ToString("D"), providerScopePath, providerProjectKey, repositoryId, pullRequestId);
    }
}
