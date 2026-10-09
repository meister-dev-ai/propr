// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>Retains conservative identity interpretation for unknown saved provider values.</summary>
internal sealed class UnregisteredScmIdentityPolicy(ScmProvider provider) : IScmIdentityPolicy
{
    public ScmProvider Provider => provider;
    public bool MatchesReviewerIdentity(ReviewerIdentity? actual, ReviewerIdentity expected) => ReviewerIdentityCoordinates.Matches(actual, expected);
    public ProviderCommentIdScope CommentIdScope => ProviderCommentIdScope.Thread;
    public bool IsMentioned(string content, ReviewerIdentity identity) => false;
    public ScmNativeAccountFacts GetAccountFacts(string externalUserId, string? login, string? displayName) => new();
    public bool IsPublicationBot(string login) => false;
}
