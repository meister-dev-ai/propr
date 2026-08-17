// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Services;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Tests.Services;

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

    /// <summary>
    ///     What a Forgejo or GitHub answer looks like on the next scan: the question quoted, the answer under
    ///     it. Reading the quote as a question would answer the answer, and quote that in turn.
    /// </summary>
    [Theory]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.GitLab)]
    [InlineData(ScmProvider.Forgejo)]
    public void IsMentioned_ItsOwnQuotedAnswer_ReturnsFalse(ScmProvider provider)
    {
        var reviewer = CreateReviewer(provider, "user-1", "meister-dev-bot");
        var quotedAnswer = "> @meister-dev-bot what does this do?\n\nIt sorts ascending and then takes three.";

        Assert.False(MentionDetector.IsMentioned(quotedAnswer, reviewer));
    }

    [Theory]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.GitLab)]
    [InlineData(ScmProvider.Forgejo)]
    public void IsMentioned_FollowUpAskedUnderAQuote_ReturnsTrue(ScmProvider provider)
    {
        var reviewer = CreateReviewer(provider, "user-1", "meister-dev-bot");
        var followUp = "> It sorts ascending and then takes three.\n\n@meister-dev-bot then why the label?";

        Assert.True(MentionDetector.IsMentioned(followUp, reviewer));
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
