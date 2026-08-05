// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.ReviewArchive;

/// <summary>
///     A single provenance mapping recorded wherever ProPR posts a comment: it links one provider-native
///     comment back to the job that posted it, scoped by the pull request the comment belongs to. The natural
///     key is (client + repository + pull request + provider comment); recording the same comment again is
///     idempotent.
/// </summary>
/// <param name="ClientId">Owning client.</param>
/// <param name="RepositoryId">Provider repository identifier.</param>
/// <param name="PullRequestId">Provider pull-request identifier.</param>
/// <param name="ProviderThreadId">Provider thread identifier, when the provider exposes one.</param>
/// <param name="ProviderCommentId">Provider-native comment identifier.</param>
/// <param name="JobId">
///     The job that posted the comment: a review job, or a mention-reply job when ProPR answered a mention.
///     The two id spaces are distinct and the row does not say which one it holds.
/// </param>
/// <param name="PostedAt">UTC timestamp when the comment was posted.</param>
public sealed record PostedCommentOriginEntry(
    Guid ClientId,
    string RepositoryId,
    long PullRequestId,
    string? ProviderThreadId,
    string ProviderCommentId,
    Guid JobId,
    DateTimeOffset PostedAt);
