// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Identity;

/// <summary>Provides GitLab account evidence and mention syntax without provider requests.</summary>
internal sealed class GitLabIdentityPolicy : IScmIdentityPolicy
{
    public ScmProvider Provider => ScmProvider.GitLab;
    public bool MatchesReviewerIdentity(ReviewerIdentity? actual, ReviewerIdentity expected) => ReviewerIdentityCoordinates.Matches(actual, expected);
    public ProviderCommentIdScope CommentIdScope => ProviderCommentIdScope.PullRequest;
    public bool IsMentioned(string content, ReviewerIdentity identity) => LoginMentionMatcher.ContainsLoginMention(content, identity.Login);

    public ScmNativeAccountFacts GetAccountFacts(string externalUserId, string? login, string? displayName) =>
        new(IsServiceAccount: IsServiceAccount(login) || IsServiceAccount(displayName));

    public bool IsPublicationBot(string login) => false;

    private static bool IsServiceAccount(string? name)
    {
        var candidate = string.IsNullOrWhiteSpace(name) ? null : name.Trim().ToLowerInvariant();
        return candidate is not null && (candidate.StartsWith("project_bot_", StringComparison.Ordinal)
                                         || candidate.StartsWith("group_bot_", StringComparison.Ordinal));
    }
}
