// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Api.Features.Reviewing.Contracts;

/// <summary>Identifies a pull request to block and, optionally, why.</summary>
/// <param name="ProviderScopePath">Provider host/organisation scope of the pull request.</param>
/// <param name="ProviderProjectKey">Provider project/namespace key of the pull request.</param>
/// <param name="RepositoryId">Repository identifier of the pull request.</param>
/// <param name="PullRequestId">Pull request number.</param>
/// <param name="Reason">Optional free-text reason for the block.</param>
public sealed record BlockPullRequestRequest(
    string ProviderScopePath,
    string ProviderProjectKey,
    string RepositoryId,
    int PullRequestId,
    string? Reason = null);

/// <summary>Identifies a pull request to unblock.</summary>
/// <param name="ProviderScopePath">Provider host/organisation scope of the pull request.</param>
/// <param name="ProviderProjectKey">Provider project/namespace key of the pull request.</param>
/// <param name="RepositoryId">Repository identifier of the pull request.</param>
/// <param name="PullRequestId">Pull request number.</param>
public sealed record UnblockPullRequestRequest(
    string ProviderScopePath,
    string ProviderProjectKey,
    string RepositoryId,
    int PullRequestId);

/// <summary>A pull request currently blocked from review processing.</summary>
/// <param name="Id">Identifier of the block record.</param>
/// <param name="ClientId">Owning client.</param>
/// <param name="ProviderScopePath">Provider host/organisation scope of the pull request.</param>
/// <param name="ProviderProjectKey">Provider project/namespace key of the pull request.</param>
/// <param name="RepositoryId">Repository identifier of the pull request.</param>
/// <param name="PullRequestId">Pull request number.</param>
/// <param name="BlockedByUserId">User who created the block.</param>
/// <param name="BlockedAt">When the block was created (UTC).</param>
/// <param name="Reason">Optional free-text reason for the block.</param>
public sealed record BlockedPullRequestDto(
    Guid Id,
    Guid ClientId,
    string ProviderScopePath,
    string ProviderProjectKey,
    string RepositoryId,
    int PullRequestId,
    Guid BlockedByUserId,
    DateTimeOffset BlockedAt,
    string? Reason);
