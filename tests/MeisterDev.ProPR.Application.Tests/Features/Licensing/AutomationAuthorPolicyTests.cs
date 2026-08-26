// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Licensing.Models;
using MeisterDev.ProPR.Application.Features.Licensing.Services;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Tests.Features.Licensing;

/// <summary>
///     The name-and-flag half of the exclusion rules, one case per signal class and per provider shape, with
///     the near misses that show the rules match accounts rather than substrings.
/// </summary>
public sealed class AutomationAuthorPolicyTests
{
    /// <summary>The flag is a statement only when it is true, and it is the one signal every provider shape shares.</summary>
    public static TheoryData<ScmProvider> EveryProvider { get; } = [.. Enum.GetValues<ScmProvider>()];

    [Theory]
    [MemberData(nameof(EveryProvider))]
    public void AProviderStatedBotFlag_IsAutomationOnEveryProvider(ScmProvider provider)
    {
        Assert.True(AutomationAuthorPolicy.IsAutomation(Observed(provider, "octo.dev", isBot: true)));
    }

    // False and absent are the same answer. The review side carries the flag from GitHub alone and the mention
    // side stores it in a column that cannot be null, so neither value states the account is a person.
    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void AFlagThatStatesNothing_LeavesTheAuthorCounted(bool? isBot)
    {
        Assert.False(AutomationAuthorPolicy.IsAutomation(Observed(ScmProvider.GitHub, "octo.dev", isBot)));
    }

    [Theory]
    [InlineData("project_bot_1234")]
    [InlineData("group_bot_release")]
    [InlineData("PROJECT_BOT_UPPER")]
    public void AGitLabServiceAccountLogin_IsAutomation(string login)
    {
        Assert.True(AutomationAuthorPolicy.IsAutomation(Observed(ScmProvider.GitLab, login)));
    }

    // The prefix is GitLab's own convention. The same login on another provider is a name somebody chose.
    [Fact]
    public void AGitLabServiceAccountLoginOnAnotherProvider_LeavesTheAuthorCounted()
    {
        Assert.False(AutomationAuthorPolicy.IsAutomation(Observed(ScmProvider.GitHub, "project_bot_1234")));
    }

    [Theory]
    [InlineData(ScmProvider.GitHub, "dependabot[bot]")]
    [InlineData(ScmProvider.GitHub, "renovate[BOT]")]
    [InlineData(ScmProvider.Forgejo, "release-please[bot]")]
    [InlineData(ScmProvider.AzureDevOps, "some-app[bot]")]
    public void ALoginEndingInTheBotSuffix_IsAutomation(ScmProvider provider, string login)
    {
        Assert.True(AutomationAuthorPolicy.IsAutomation(Observed(provider, login)));
    }

    [Theory]
    [InlineData("dependabot")]
    [InlineData("Dependabot")]
    [InlineData("dependabot-preview")]
    [InlineData("renovate")]
    [InlineData("renovate-bot")]
    [InlineData("github-actions")]
    [InlineData("release-please")]
    public void AKnownAutomationLogin_IsAutomation(string login)
    {
        Assert.True(AutomationAuthorPolicy.IsAutomation(Observed(ScmProvider.Forgejo, login)));
    }

    // The two space-separated entries are display names rather than logins, because a provider does not allow
    // a space in a login.
    [Theory]
    [InlineData("Renovate Bot")]
    [InlineData("GitHub Actions")]
    public void AKnownAutomationNameWithASpace_IsAutomationThroughTheDisplayName(string displayName)
    {
        var observation = new AuthorActivityObservation(
            Host(ScmProvider.GitLab),
            "4242",
            AuthorActivitySource.MentionAnswer,
            "svc-account",
            displayName);

        Assert.True(AutomationAuthorPolicy.IsAutomation(observation));
    }

    // The mention side stores the one name a comment payload carries as both the login and the display name,
    // so a display name has to be able to carry the signal on its own.
    [Fact]
    public void AKnownAutomationDisplayName_IsAutomation()
    {
        var observation = new AuthorActivityObservation(
            Host(ScmProvider.GitLab),
            "4242",
            AuthorActivitySource.MentionAnswer,
            "svc-account",
            "GitHub Actions");

        Assert.True(AutomationAuthorPolicy.IsAutomation(observation));
    }

    // The near misses. Each contains or resembles a name on the list and is a person.
    [Theory]
    [InlineData("dependable-dev")]
    [InlineData("dependabot-watcher")]
    [InlineData("renovations")]
    [InlineData("robot")]
    [InlineData("botany")]
    [InlineData("release-pleaser")]
    public void ALoginThatMerelyResemblesAutomation_LeavesTheAuthorCounted(string login)
    {
        Assert.False(AutomationAuthorPolicy.IsAutomation(Observed(ScmProvider.GitHub, login)));
    }

    [Theory]
    [InlineData("Project Collection Build Service (acme)")]
    [InlineData("Contoso Web Build Service (contoso)")]
    public void AnAzureDevOpsBuildServiceName_IsAutomation(string displayName)
    {
        var observation = new AuthorActivityObservation(
            Host(ScmProvider.AzureDevOps),
            "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0",
            AuthorActivitySource.Review,
            displayName,
            displayName);

        Assert.True(AutomationAuthorPolicy.IsAutomation(observation));
    }

    // The project-scoped shape has to be the whole tail of the name, so a name that merely mentions it in the
    // middle is a person's.
    [Fact]
    public void AnAzureDevOpsNameMentioningABuildServiceInTheMiddle_LeavesTheAuthorCounted()
    {
        var observation = new AuthorActivityObservation(
            Host(ScmProvider.AzureDevOps),
            "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0",
            AuthorActivitySource.Review,
            "dana.lee@acme.example",
            "Dana Lee, Build Service (contoso) administrator");

        Assert.False(AutomationAuthorPolicy.IsAutomation(observation));
    }

    [Fact]
    public void AnAzureDevOpsAccountNamedAfterAPerson_LeavesTheAuthorCounted()
    {
        var observation = new AuthorActivityObservation(
            Host(ScmProvider.AzureDevOps),
            "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0",
            AuthorActivitySource.Review,
            "octo.dev@acme.example",
            "Octo Dev");

        Assert.False(AutomationAuthorPolicy.IsAutomation(observation));
    }

    // Forgejo and Gitea report one placeholder account for every deleted user. It is not a person, and it is
    // not one person either.
    [Fact]
    public void TheForgejoDeletedAccountPlaceholder_IsAutomation()
    {
        var observation = new AuthorActivityObservation(
            Host(ScmProvider.Forgejo),
            "-1",
            AuthorActivitySource.Review,
            "Ghost",
            "Ghost");

        Assert.True(AutomationAuthorPolicy.IsAutomation(observation));
    }

    // The identifier is Forgejo's placeholder only on Forgejo. Nothing states that another provider issues it,
    // and treating it as a placeholder everywhere would drop an author on a guess.
    [Fact]
    public void TheSameIdentifierOnAnotherProvider_LeavesTheAuthorCounted()
    {
        var observation = new AuthorActivityObservation(
            Host(ScmProvider.GitLab),
            "-1",
            AuthorActivitySource.Review,
            "octo.dev",
            "Octo Dev");

        Assert.False(AutomationAuthorPolicy.IsAutomation(observation));
    }

    [Fact]
    public void AnObservationWithoutNames_LeavesTheAuthorCounted()
    {
        var observation = new AuthorActivityObservation(
            Host(ScmProvider.AzureDevOps),
            "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0",
            AuthorActivitySource.Review);

        Assert.False(AutomationAuthorPolicy.IsAutomation(observation));
    }

    private static ProviderHostRef Host(ScmProvider provider)
    {
        return provider switch
        {
            ScmProvider.AzureDevOps => new ProviderHostRef(provider, "https://dev.azure.com/acme"),
            ScmProvider.GitHub => new ProviderHostRef(provider, "https://github.com"),
            ScmProvider.GitLab => new ProviderHostRef(provider, "https://gitlab.example.com"),
            _ => new ProviderHostRef(provider, "https://forgejo.example.com"),
        };
    }

    /// <summary>One observation whose login and display name are the same name, which is the mention shape.</summary>
    private static AuthorActivityObservation Observed(ScmProvider provider, string login, bool? isBot = null)
    {
        return new AuthorActivityObservation(
            Host(provider),
            "4242",
            AuthorActivitySource.Review,
            login,
            login,
            isBot);
    }
}
