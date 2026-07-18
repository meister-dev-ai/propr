// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Entities;

namespace MeisterProPR.Application.Interfaces;

/// <summary>
///     Persistence boundary for pull-request block records. A block prevents both review-intake paths
///     (direct submission and crawl/webhook synchronization) from creating new review jobs for the pull
///     request, while leaving any already-running job untouched. The pull-request identity is the same
///     tuple both intake paths and the pull-request view key a PR by.
/// </summary>
public interface IBlockedPullRequestStore
{
    /// <summary>Returns whether the pull request is currently blocked for the client.</summary>
    Task<bool> IsBlockedAsync(
        Guid clientId,
        string providerScopePath,
        string providerProjectKey,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct = default);

    /// <summary>
    ///     Blocks the pull request if it is not already blocked. Returns <see langword="true" /> when a new
    ///     block was created and <see langword="false" /> when one already existed (idempotent).
    /// </summary>
    Task<bool> BlockAsync(
        Guid clientId,
        string providerScopePath,
        string providerProjectKey,
        string repositoryId,
        int pullRequestId,
        Guid blockedByUserId,
        string? reason,
        CancellationToken ct = default);

    /// <summary>
    ///     Removes any block for the pull request. Returns <see langword="true" /> when a block was removed
    ///     and <see langword="false" /> when none existed (idempotent).
    /// </summary>
    Task<bool> UnblockAsync(
        Guid clientId,
        string providerScopePath,
        string providerProjectKey,
        string repositoryId,
        int pullRequestId,
        CancellationToken ct = default);

    /// <summary>Lists all blocked pull requests for the client.</summary>
    Task<IReadOnlyList<BlockedPullRequest>> ListForClientAsync(Guid clientId, CancellationToken ct = default);
}
