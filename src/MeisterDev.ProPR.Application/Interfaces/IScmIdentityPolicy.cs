// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>Interprets native identity evidence without credentials or provider requests.</summary>
public interface IScmIdentityPolicy
{
    ScmProvider Provider { get; }
    ProviderCommentIdScope CommentIdScope { get; }
    bool IsMentioned(string content, ReviewerIdentity identity);
    ScmNativeAccountFacts GetAccountFacts(string externalUserId, string? login, string? displayName);
    bool IsPublicationBot(string login);
    bool MatchesReviewerIdentity(ReviewerIdentity? actual, ReviewerIdentity expected);
    bool CanDeriveAutomaticReviewerIdentity => false;
    bool IsAutomaticReviewerIdentityEligible(ScmAuthenticationKind authenticationKind) => false;
}

/// <summary>Contains native account evidence; consumers retain their own fixed decision policies.</summary>
public sealed record ScmNativeAccountFacts(bool IsBuildService = false, bool IsServiceAccount = false, bool IsDeletedAccount = false);
