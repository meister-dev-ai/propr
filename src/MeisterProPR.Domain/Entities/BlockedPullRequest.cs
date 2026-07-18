// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Marks a single pull request as blocked from review processing for a client. While a block exists,
///     incoming review submissions and crawl/webhook pushes for the same pull request create no new review
///     jobs. A block does not stop a job that is already running — halting an in-flight review is a separate
///     stop action. One row per (ClientId, ProviderScopePath, ProviderProjectKey, RepositoryId,
///     PullRequestId) — the same identity the pull-request view and both intake paths key a PR by.
/// </summary>
public sealed class BlockedPullRequest
{
    /// <summary>Creates a new <see cref="BlockedPullRequest" />.</summary>
    /// <param name="id">Unique identifier — must not be <see cref="Guid.Empty" />.</param>
    /// <param name="clientId">Owning client identifier — must not be <see cref="Guid.Empty" />.</param>
    /// <param name="providerScopePath">Provider host/organisation scope — must not be null or whitespace.</param>
    /// <param name="providerProjectKey">Provider project/namespace key — must not be null or whitespace.</param>
    /// <param name="repositoryId">Repository identifier — must not be null or whitespace.</param>
    /// <param name="pullRequestId">Pull request number — must be greater than zero.</param>
    /// <param name="blockedByUserId">User who created the block — must not be <see cref="Guid.Empty" />.</param>
    /// <param name="reason">Optional free-text reason for the block.</param>
    public BlockedPullRequest(
        Guid id,
        Guid clientId,
        string providerScopePath,
        string providerProjectKey,
        string repositoryId,
        int pullRequestId,
        Guid blockedByUserId,
        string? reason = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", nameof(id));
        }

        if (clientId == Guid.Empty)
        {
            throw new ArgumentException("ClientId must not be empty.", nameof(clientId));
        }

        if (string.IsNullOrWhiteSpace(providerScopePath))
        {
            throw new ArgumentException("ProviderScopePath must not be null or whitespace.", nameof(providerScopePath));
        }

        if (string.IsNullOrWhiteSpace(providerProjectKey))
        {
            throw new ArgumentException("ProviderProjectKey must not be null or whitespace.", nameof(providerProjectKey));
        }

        if (string.IsNullOrWhiteSpace(repositoryId))
        {
            throw new ArgumentException("RepositoryId must not be null or whitespace.", nameof(repositoryId));
        }

        if (pullRequestId < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pullRequestId), "PullRequestId must be greater than zero.");
        }

        if (blockedByUserId == Guid.Empty)
        {
            throw new ArgumentException("BlockedByUserId must not be empty.", nameof(blockedByUserId));
        }

        this.Id = id;
        this.ClientId = clientId;
        this.ProviderScopePath = providerScopePath;
        this.ProviderProjectKey = providerProjectKey;
        this.RepositoryId = repositoryId;
        this.PullRequestId = pullRequestId;
        this.BlockedByUserId = blockedByUserId;
        this.Reason = reason;
        this.BlockedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Unique identifier.</summary>
    public Guid Id { get; init; }

    /// <summary>FK to the client that owns this block.</summary>
    public Guid ClientId { get; init; }

    /// <summary>Provider host/organisation scope (maps to the review job's organisation URL).</summary>
    public string ProviderScopePath { get; init; }

    /// <summary>Provider project/namespace key (maps to the review job's project id).</summary>
    public string ProviderProjectKey { get; init; }

    /// <summary>Repository identifier.</summary>
    public string RepositoryId { get; init; }

    /// <summary>Pull request number.</summary>
    public int PullRequestId { get; init; }

    /// <summary>User who created the block.</summary>
    public Guid BlockedByUserId { get; init; }

    /// <summary>Optional free-text reason for the block.</summary>
    public string? Reason { get; init; }

    /// <summary>When the block was created (UTC).</summary>
    public DateTimeOffset BlockedAt { get; init; }
}
