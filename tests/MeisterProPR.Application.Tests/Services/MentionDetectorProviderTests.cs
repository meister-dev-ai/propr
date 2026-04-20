// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Services;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;

namespace MeisterProPR.Application.Tests.Services;

public sealed class MentionDetectorProviderTests
{
    [Fact]
    public void IsMentioned_WithGitHubLoginMention_ReturnsTrue()
    {
        var reviewer = CreateReviewer(ScmProvider.GitHub, "github-user-1", "meister-dev-bot");

        Assert.True(MentionDetector.IsMentioned("Please check this again, @Meister-Dev-Bot.", reviewer));
    }

    [Fact]
    public void IsMentioned_WithGitLabLoginMention_ReturnsTrue()
    {
        var reviewer = CreateReviewer(ScmProvider.GitLab, "gitlab-user-1", "meister_dev_bot");

        Assert.True(MentionDetector.IsMentioned("/cc @meister_dev_bot for follow-up", reviewer));
    }

    [Fact]
    public void IsMentioned_WithForgejoLoginEmbeddedInEmail_ReturnsFalse()
    {
        var reviewer = CreateReviewer(ScmProvider.Forgejo, "forgejo-user-1", "meister-dev-bot");

        Assert.False(MentionDetector.IsMentioned("notify meister@meister-dev-bot.example when this lands", reviewer));
    }

    [Fact]
    public void IsMentioned_WithAzureDevOpsReviewerIdentity_ReturnsTrue()
    {
        var reviewerGuid = Guid.Parse("0caeb875-08d2-6d69-88fb-302b06d21993");
        var reviewer = CreateReviewer(ScmProvider.AzureDevOps, reviewerGuid.ToString("D"), "ado-bot");

        Assert.True(
            MentionDetector.IsMentioned(
                $"@<{reviewerGuid.ToString().ToUpperInvariant()}> What do you think?",
                reviewer));
    }

    [Fact]
    public void IsMentioned_WithEmptyContent_ReturnsFalse()
    {
        var reviewer = CreateReviewer(ScmProvider.GitHub, "github-user-1", "meister-dev-bot");

        Assert.False(MentionDetector.IsMentioned(string.Empty, reviewer));
    }

    private static ReviewerIdentity CreateReviewer(ScmProvider provider, string externalUserId, string login)
    {
        var hostBaseUrl = provider switch
        {
            ScmProvider.AzureDevOps => "https://dev.azure.com/org",
            ScmProvider.GitHub => "https://github.com",
            ScmProvider.GitLab => "https://gitlab.com",
            ScmProvider.Forgejo => "https://codeberg.org",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        };

        return new ReviewerIdentity(
            new ProviderHostRef(provider, hostBaseUrl),
            externalUserId,
            login,
            "Meister Dev Bot",
            true);
    }
}
