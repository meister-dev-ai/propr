// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Clients.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Security;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Security;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Security;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers;

public sealed class ScmConnectionConfigurationPolicyTests
{
    [Fact]
    public void NativeObservedConnectionMatchingRemainsAuthorityOnly()
    {
        var policy = Policy(ScmProvider.AzureDevOps);
        Assert.True(policy.MatchesObservedConnectionHost("https://server.test/tfs/sibling", "https://server.test/tfs/team"));
        Assert.False(policy.MatchesObservedConnectionHost("https://server.test:8443/tfs/team", "https://server.test/tfs/team"));
        Assert.True(policy.MatchesObservedConnectionHost("https://user:pass@server.test./tfs/sibling", "https://server.test/tfs/team"));
    }

    [Fact]
    public void NativeCompatibilityInferenceIsSeparateFromGeneralHostedClassification()
    {
        Assert.True(Policy(ScmProvider.AzureDevOps).MatchesCompatibilityScope("https://org.visualstudio.com/path"));
        Assert.False(Policy(ScmProvider.AzureDevOps).MatchesCompatibilityScope("https://server.test/tfs/team"));
        Assert.False(Policy(ScmProvider.GitHub).MatchesCompatibilityScope("https://github.com/owner"));
    }

    [Fact]
    public void OfflineCompositionProvidesConfigurationFactsWithoutLiveCapabilities()
    {
        var services = new ServiceCollection();
        services.AddReviewEvalHarness(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<IScmProviderRegistry>();
        Assert.Equal("hosted", registry.GetConnectionConfigurationPolicy(ScmProvider.GitHub).ResolveHostVariant("https://github.com"));
        Assert.False(registry.IsRegistered(ScmProvider.GitHub));
        Assert.Throws<InvalidOperationException>(() => registry.GetCodeReviewQueryService(ScmProvider.GitHub));
    }

    [Theory]
    [InlineData(ScmProvider.AzureDevOps, "https://user:pass@server.test/tfs/team?x=y#fragment", "https://user:pass@server.test/tfs/team")]
    [InlineData(ScmProvider.AzureDevOps, "https://dev.azure.com/team?x=y", "https://dev.azure.com")]
    [InlineData(ScmProvider.GitHub, "https://host.test/base", "https://host.test")]
    [InlineData(ScmProvider.GitLab, "https://host.test/base", "https://host.test")]
    [InlineData(ScmProvider.Forgejo, "https://host.test/base", "https://host.test")]
    public void SavedConnectionHostProjectionKeepsItsExistingBytes(ScmProvider provider, string host, string expected)
    {
        Assert.Equal(expected, Policy(provider).NormalizeConnectionHost(host));
    }

    [Fact]
    public void ProviderlessWindowsPatchPrevalidationIsDeclaredByNativePolicy()
    {
        var errors = Policy(ScmProvider.AzureDevOps).ValidateCompatibilityPatchRequest(ScmAuthenticationKind.WindowsUserAccount, null);
        Assert.Equal("", Assert.Single(errors).PropertyName);
        Assert.Equal(
            "UserName must be provided when switching to Azure DevOps Server Windows user-account authentication.",
            errors[0].Message);
    }

    [Fact]
    public void NativeCreateValidationHasNoSharedProviderDecisionOwner()
    {
        Assert.Null(
            typeof(AdoConnectionConfigurationPolicy).Assembly.GetType(
                "MeisterDev.ProPR.Infrastructure.Features.Providers.Common.ScmConnectionCreateValidation"));
    }

    [Theory]
    [InlineData(ScmProvider.AzureDevOps, "https://org.visualstudio.com/path", "hosted")]
    [InlineData(ScmProvider.AzureDevOps, "https://dev.azure.com/team", "hosted")]
    [InlineData(ScmProvider.GitHub, "https://github.com/owner", "hosted")]
    [InlineData(ScmProvider.GitLab, "https://gitlab.com/group", "hosted")]
    [InlineData(ScmProvider.Forgejo, "https://codeberg.org/owner", "hosted")]
    [InlineData(ScmProvider.GitHub, "https://enterprise.test", "selfHosted")]
    [InlineData(ScmProvider.AzureDevOps, "invalid", "selfHosted")]
    public void CredentialFreePolicyOwnsNativeHostClassification(ScmProvider provider, string host, string expected)
    {
        Assert.Equal(expected, Policy(provider).ResolveHostVariant(host));
    }

    [Fact]
    public void LocalGitHubPolicyProvidesCategorizedVerificationDiagnostics()
    {
        var connection = new MeisterDev.ProPR.Application.DTOs.ClientScmConnectionDto(
            Guid.NewGuid(), Guid.NewGuid(), ScmProvider.GitHub, "https://github.test",
            ScmAuthenticationKind.AppInstallation, "app", true, "failed", null,
            "unsafe detailed error", "authentication", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.Equal(
            "GitHub App verification failed. Check the saved App ID, installation ID, private key, and granted permissions.",
            Policy(ScmProvider.GitHub).GetVerificationReadinessReason(connection, "failed"));
    }

    [Theory]
    [InlineData(ScmProvider.AzureDevOps)]
    [InlineData(ScmProvider.GitHub)]
    [InlineData(ScmProvider.GitLab)]
    [InlineData(ScmProvider.Forgejo)]
    public void PersonalAccessTokenMetadataRemainsValid(ScmProvider provider)
    {
        var state = new ScmAuthenticationConfiguration(provider, "https://provider.example", ScmAuthenticationKind.PersonalAccessToken, null, null, null);
        Assert.Empty(Policy(provider).Validate(state));
    }

    [Theory]
    [InlineData("https://dev.azure.com", true)]
    [InlineData("https://org.visualstudio.com", true)]
    [InlineData("https://server.example", false)]
    [InlineData("https://dev.azure.com.example", false)]
    public void HostedClassificationPreservesOAuthAcceptance(string host, bool accepted)
    {
        var state = new ScmAuthenticationConfiguration(ScmProvider.AzureDevOps, host, ScmAuthenticationKind.OAuthClientCredentials, null, "tenant", "client");
        Assert.Equal(accepted, Policy(state.ProviderFamily).Validate(state).All(error => error.PropertyName != "AuthenticationKind"));
    }

    [Fact]
    public void AppTransitionRequiresMetadataAndReplacementSecret()
    {
        var state = new ScmAuthenticationConfiguration(
            ScmProvider.GitHub, "https://github.example", ScmAuthenticationKind.AppInstallation, null, null, null, HasCompatibleSecretMaterial: false);
        Assert.Equal(
            new[] { "GitHubAppId", "GitHubAppInstallationId", "Secret" }, Policy(state.ProviderFamily).Validate(state).Select(error => error.PropertyName));
    }

    [Fact]
    public void ServerPatRequiresHttpsWithoutRejectingOtherProviderHosts()
    {
        foreach (var provider in Enum.GetValues<ScmProvider>())
        {
            var state = new ScmAuthenticationConfiguration(provider, "http://provider.example", ScmAuthenticationKind.PersonalAccessToken, null, null, null);
            Assert.Equal(provider == ScmProvider.AzureDevOps, Policy(provider).Validate(state).Any(error => error.PropertyName == "HostBaseUrl"));
        }
    }

    [Fact]
    public void CreateServerWindowsHttpsErrorsRetainModelLevelFieldKeys()
    {
        var state = new ScmAuthenticationConfiguration(
            ScmProvider.AzureDevOps, "http://server.example", ScmAuthenticationKind.WindowsUserAccount, "user", null, null);
        var errors = Policy(state.ProviderFamily).ValidateCreate(state);
        Assert.Equal(2, errors.Count);
        Assert.All(errors, error => Assert.Equal("", error.PropertyName));
    }

    [Fact]
    public void UndefinedFamilyRetainsRestrictedMetadataValidationBeforeActivation()
    {
        var policy = new UnregisteredScmConnectionConfigurationPolicy((ScmProvider)999);
        var state = new ScmAuthenticationConfiguration(
            (ScmProvider)999, "https://provider.example", ScmAuthenticationKind.PersonalAccessToken, null, null, null);
        Assert.Empty(policy.ValidateCreate(state));
        var errors = policy.ValidateCreate(state with { AuthenticationKind = ScmAuthenticationKind.OAuthClientCredentials });
        Assert.Equal("999 provider connections currently use a restricted authentication model.", Assert.Single(errors).Message);
    }

    private static IScmConnectionConfigurationPolicy Policy(ScmProvider provider) => provider switch
    {
        ScmProvider.AzureDevOps => new AdoConnectionConfigurationPolicy(),
        ScmProvider.GitHub => new GitHubConnectionConfigurationPolicy(),
        ScmProvider.GitLab => new GitLabConnectionConfigurationPolicy(),
        ScmProvider.Forgejo => new ForgejoConnectionConfigurationPolicy(),
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };
}
