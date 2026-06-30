// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.ReviewArchive;

/// <summary>
///     Identifies a single retained pull request across the review-archive store. This is the natural
///     key used by every store operation; it does not reference review or memory tables.
/// </summary>
/// <param name="ClientId">Owning client.</param>
/// <param name="ConnectionId">SCM provider connection the pull request belongs to.</param>
/// <param name="RepositoryId">Provider repository identifier.</param>
/// <param name="PullRequestId">Provider pull-request identifier.</param>
public sealed record PullRequestRetentionKey(
    Guid ClientId,
    Guid ConnectionId,
    string RepositoryId,
    long PullRequestId);
