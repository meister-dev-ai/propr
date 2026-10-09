// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Clients.Support;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.Features.Clients.Models;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Clients;

public sealed class ProviderReadinessProfileCatalogTests
{
    [Fact]
    public void SyntheticProviderUsesDeclaredLocalEvidenceWithoutLiveRegistration()
    {
        var provider = (ScmProvider)792;
        var policy = Substitute.For<IScmConnectionConfigurationPolicy>();
        policy.Provider.Returns(provider);
        policy.ResolveHostVariant("https://synthetic.example").Returns("local");
        var evidence = new ProviderReadinessProfile(provider, "local", true, true, true, true, true, "Local declaration.");
        policy.ReadinessProfiles.Returns([evidence]);

        var catalog = new StaticProviderReadinessProfileCatalog([policy]);

        Assert.Same(evidence, catalog.GetProfile(provider, "https://synthetic.example"));
        Assert.Same(evidence, Assert.Single(catalog.GetProfiles(provider)));
    }

    [Fact]
    public void GetProfile_GitHubHosted_ReturnsWorkflowCompleteProfile()
    {
        var sut = new StaticProviderReadinessProfileCatalog(MeisterDev.ProPR.TestSupport.LocalScmPolicies.ConfigurationPolicies);

        var profile = sut.GetProfile(ScmProvider.GitHub, "https://github.com/acme/platform");

        Assert.Equal("hosted", profile.HostVariant);
        Assert.True(profile.IsWorkflowComplete);
    }

    [Fact]
    public void GetProfile_GitHubSelfHosted_ReturnsOnboardingReadyProfile()
    {
        var sut = new StaticProviderReadinessProfileCatalog(MeisterDev.ProPR.TestSupport.LocalScmPolicies.ConfigurationPolicies);

        var profile = sut.GetProfile(ScmProvider.GitHub, "https://github.enterprise.example.com/acme/platform");

        Assert.Equal("selfHosted", profile.HostVariant);
        Assert.False(profile.IsWorkflowComplete);
    }

    [Fact]
    public void GetProfile_AzureDevOpsSelfHosted_ReturnsOnboardingReadyProfile()
    {
        var sut = new StaticProviderReadinessProfileCatalog(MeisterDev.ProPR.TestSupport.LocalScmPolicies.ConfigurationPolicies);

        var profile = sut.GetProfile(ScmProvider.AzureDevOps, "https://ado-server.example.com/tfs/defaultcollection");

        Assert.Equal("selfHosted", profile.HostVariant);
        Assert.False(profile.IsWorkflowComplete);
        Assert.Contains("Self-hosted Azure DevOps", profile.Notes, StringComparison.OrdinalIgnoreCase);
    }
}
