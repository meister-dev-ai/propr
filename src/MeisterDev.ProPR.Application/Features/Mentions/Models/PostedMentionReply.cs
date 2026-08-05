// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Mentions.Models;

/// <summary>
///     One mention answer that reached the pull request, read back from the completed job that posted it.
///     Everything a provenance row needs is here, which is the point: the origin row is derivable from
///     persisted state alone, so losing the write no longer loses the attribution.
/// </summary>
/// <param name="JobId">The mention-reply job that posted the answer.</param>
/// <param name="ClientId">Owning client.</param>
/// <param name="RepositoryId">Provider repository identifier.</param>
/// <param name="PullRequestId">Provider pull-request identifier.</param>
/// <param name="ProviderThreadId">Provider thread the answer was posted into.</param>
/// <param name="ProviderCommentId">Provider-native identifier of the posted answer.</param>
/// <param name="PostedAt">
///     When the job completed, which is the same moment the answer went out: the completion update is what
///     records the comment id, and it runs immediately after the post returns.
/// </param>
public sealed record PostedMentionReply(
    Guid JobId,
    Guid ClientId,
    string RepositoryId,
    long PullRequestId,
    string ProviderThreadId,
    string ProviderCommentId,
    DateTimeOffset PostedAt);
