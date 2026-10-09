// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Identity;

/// <summary>Provides GitHub account evidence and mention syntax without provider requests.</summary>
internal sealed class GitHubIdentityPolicy : IScmIdentityPolicy
{
    public ScmProvider Provider => ScmProvider.GitHub;
    public bool MatchesReviewerIdentity(ReviewerIdentity? actual, ReviewerIdentity expected) => ReviewerIdentityCoordinates.Matches(actual, expected);
    public bool CanDeriveAutomaticReviewerIdentity => true;
    public bool IsAutomaticReviewerIdentityEligible(ScmAuthenticationKind authenticationKind) => IsAutomaticAuthentication(authenticationKind);
    internal static bool IsAutomaticAuthentication(ScmAuthenticationKind authenticationKind) => authenticationKind == ScmAuthenticationKind.AppInstallation;
    public ProviderCommentIdScope CommentIdScope => ProviderCommentIdScope.PullRequest;
    public bool IsMentioned(string content, ReviewerIdentity identity) => LoginMentionMatcher.ContainsLoginMention(content, identity.Login);
    public ScmNativeAccountFacts GetAccountFacts(string externalUserId, string? login, string? displayName) => new();
    public bool IsPublicationBot(string login) => login.EndsWith("[bot]", StringComparison.OrdinalIgnoreCase);
}
