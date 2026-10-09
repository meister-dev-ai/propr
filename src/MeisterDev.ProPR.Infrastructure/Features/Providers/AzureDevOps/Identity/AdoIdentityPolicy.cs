// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Features.ThreadOwnership;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Identity;

/// <summary>Provides AzureDevOps account evidence and mention syntax without provider requests.</summary>
internal sealed class AdoIdentityPolicy : IScmIdentityPolicy
{
    public ScmProvider Provider => ScmProvider.AzureDevOps;
    public bool MatchesReviewerIdentity(ReviewerIdentity? actual, ReviewerIdentity expected) => ReviewerIdentityCoordinates.Matches(actual, expected);
    public ProviderCommentIdScope CommentIdScope => ProviderCommentIdScope.Thread;

    public bool IsMentioned(string content, ReviewerIdentity identity) =>
        Guid.TryParse(identity.ExternalUserId, out var id) && content.Contains($"@<{id}>", StringComparison.OrdinalIgnoreCase);

    public ScmNativeAccountFacts GetAccountFacts(string externalUserId, string? login, string? displayName) =>
        new(IsBuildService: IsBuildService(login) || IsBuildService(displayName));

    public bool IsPublicationBot(string login) => false;

    private static bool IsBuildService(string? name)
    {
        var candidate = string.IsNullOrWhiteSpace(name) ? null : name.Trim().ToLowerInvariant();
        return candidate is not null && (candidate.StartsWith("project collection build service", StringComparison.Ordinal)
                                         || candidate.Contains(" build service (", StringComparison.Ordinal) && candidate.EndsWith(')'));
    }
}
