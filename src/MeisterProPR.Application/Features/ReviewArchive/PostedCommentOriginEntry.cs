// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Features.ReviewArchive;

/// <summary>
///     A single provenance mapping recorded when a review job publishes findings: it links one
///     provider-native comment back to the job that posted it, scoped by the pull request the comment
///     belongs to. The natural key is (client + repository + pull request + provider comment); recording
///     the same comment again is idempotent.
/// </summary>
/// <param name="ClientId">Owning client.</param>
/// <param name="RepositoryId">Provider repository identifier.</param>
/// <param name="PullRequestId">Provider pull-request identifier.</param>
/// <param name="ProviderThreadId">Provider thread identifier, when the provider exposes one.</param>
/// <param name="ProviderCommentId">Provider-native comment identifier.</param>
/// <param name="JobId">Review job that posted the comment.</param>
/// <param name="PostedAt">UTC timestamp when the comment was posted.</param>
public sealed record PostedCommentOriginEntry(
    Guid ClientId,
    string RepositoryId,
    long PullRequestId,
    string? ProviderThreadId,
    string ProviderCommentId,
    Guid JobId,
    DateTimeOffset PostedAt);
