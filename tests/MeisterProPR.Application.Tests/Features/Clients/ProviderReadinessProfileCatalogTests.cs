// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Clients.Support;
using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Tests.Features.Clients;

public sealed class ProviderReadinessProfileCatalogTests
{
    [Fact]
    public void GetProfile_GitHubHosted_ReturnsWorkflowCompleteProfile()
    {
        var sut = new StaticProviderReadinessProfileCatalog();

        var profile = sut.GetProfile(ScmProvider.GitHub, "https://github.com/acme/platform");

        Assert.Equal("hosted", profile.HostVariant);
        Assert.True(profile.IsWorkflowComplete);
    }

    [Fact]
    public void GetProfile_GitHubSelfHosted_ReturnsOnboardingReadyProfile()
    {
        var sut = new StaticProviderReadinessProfileCatalog();

        var profile = sut.GetProfile(ScmProvider.GitHub, "https://github.enterprise.example.com/acme/platform");

        Assert.Equal("selfHosted", profile.HostVariant);
        Assert.False(profile.IsWorkflowComplete);
    }
}
