// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using NSubstitute;
using MeisterDev.ProPR.TestSupport;

namespace MeisterDev.ProPR.Infrastructure.Tests.Providers.Common;

public sealed class ProviderCompatibilityResolutionTests
{
    [Theory]
    [InlineData("repository configuration")]
    [InlineData("reviewer thread status")]
    public async Task AmbiguousProviderDiagnosticsKeepTheirCallerContext(string context)
    {
        var clientId = Guid.NewGuid();
        var repository = Substitute.For<IClientScmConnectionRepository>();
        repository.GetByClientIdAsync(clientId, Arg.Any<CancellationToken>())
            .Returns([Connection(clientId, ScmProvider.GitHub), Connection(clientId, ScmProvider.GitLab)]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProviderResolutionUtilities.ResolveProviderAsync(
                "https://scm.example/path", clientId, repository,
                CancellationToken.None, ambiguityContext: context));

        Assert.Equal(
            $"Multiple active SCM providers share host https://scm.example for client {clientId}. The {context} provider is ambiguous.", exception.Message);
    }

    [Fact]
    public async Task SavedConnectionProviderPrecedesNativeCompatibilityInference()
    {
        var clientId = Guid.NewGuid();
        var repository = Substitute.For<IClientScmConnectionRepository>();
        repository.GetByClientIdAsync(clientId, Arg.Any<CancellationToken>())
            .Returns([Connection(clientId, ScmProvider.GitLab) with { HostBaseUrl = "https://dev.azure.com" }]);

        Assert.Equal(
            ScmProvider.GitLab,
            await ProviderResolutionUtilities.ResolveProviderAsync("https://dev.azure.com/org", clientId, repository, CancellationToken.None));
    }

    [Fact]
    public async Task CompatibilityInferenceConsumesOnlyTheSuppliedLocalDeclaration()
    {
        var repository = Substitute.For<IClientScmConnectionRepository>();
        var clientId = Guid.NewGuid();
        repository.GetByClientIdAsync(clientId, Arg.Any<CancellationToken>()).Returns([]);
        var policy = Substitute.For<IScmConnectionConfigurationPolicy>();
        policy.Provider.Returns(ScmProvider.GitHub);
        policy.MatchesCompatibilityScope("https://scm.example/path").Returns(true);

        Assert.Equal(
            ScmProvider.GitHub,
            await ProviderResolutionUtilities.ResolveProviderAsync("https://scm.example/path", clientId, repository, CancellationToken.None, [policy]));
        policy.Received(1).MatchesCompatibilityScope("https://scm.example/path");
    }

    [Theory]
    [InlineData("https://dev.azure.com/org")]
    [InlineData("https://org.visualstudio.com/path")]
    public async Task NativeHostedCompatibilityReadsRemainAvailableWithoutAConnection(string scope)
    {
        var repository = Substitute.For<IClientScmConnectionRepository>();
        var clientId = Guid.NewGuid();
        repository.GetByClientIdAsync(clientId, Arg.Any<CancellationToken>()).Returns([]);
        Assert.Equal(
            ScmProvider.AzureDevOps,
            await ProviderResolutionUtilities.ResolveProviderAsync(
                scope, clientId, repository, CancellationToken.None, LocalScmPolicies.ConfigurationPolicies));
    }

    [Fact]
    public async Task ProviderlessCompatibilityDefaultDoesNotParseTheSource()
    {
        Assert.Equal(ScmProvider.AzureDevOps, await ProviderResolutionUtilities.ResolveProviderAsync("invalid", null, null, CancellationToken.None));
    }

    private static ClientScmConnectionDto Connection(Guid clientId, ScmProvider provider) =>
        new(
            Guid.NewGuid(), clientId, provider, "https://scm.example",
            ScmAuthenticationKind.PersonalAccessToken, "SCM", true, "verified", null, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
