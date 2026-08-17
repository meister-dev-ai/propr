// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Providers.Common;

public sealed class ProviderActivePrFetcherTests
{
    [Fact]
    public async Task GetRecentlyUpdatedPullRequestsAsync_DispatchesToTheProviderTheQueryNames()
    {
        var gitHub = CreateProvider(ScmProvider.GitHub);
        var azureDevOps = CreateProvider(ScmProvider.AzureDevOps);
        var expected = new ActivePullRequestRef("https://github.com", "acme", "101", 7, DateTimeOffset.UtcNow);
        var query = CreateQuery(ScmProvider.GitHub);

        gitHub.GetRecentlyUpdatedPullRequestsAsync(query, Arg.Any<CancellationToken>())
            .Returns(new ActivePullRequestDiscovery([expected], true));

        var sut = new ProviderActivePrFetcher([gitHub, azureDevOps]);

        var result = await sut.GetRecentlyUpdatedPullRequestsAsync(query);

        Assert.Equal([expected], result.PullRequests);

        // Carried through rather than re-decided here: only the adapter knows whether it read everything.
        Assert.True(result.IsComplete);
        await azureDevOps.DidNotReceiveWithAnyArgs()
            .GetRecentlyUpdatedPullRequestsAsync(null!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetRecentlyUpdatedPullRequestsAsync_UnregisteredProvider_ReportsItRatherThanUsingAnother()
    {
        var azureDevOps = CreateProvider(ScmProvider.AzureDevOps);
        var sut = new ProviderActivePrFetcher([azureDevOps]);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.GetRecentlyUpdatedPullRequestsAsync(CreateQuery(ScmProvider.Forgejo)));

        Assert.Contains("Forgejo", failure.Message, StringComparison.Ordinal);
        await azureDevOps.DidNotReceiveWithAnyArgs()
            .GetRecentlyUpdatedPullRequestsAsync(null!, Arg.Any<CancellationToken>());
    }

    private static IActivePullRequestDiscoveryProvider CreateProvider(ScmProvider provider)
    {
        var discoveryProvider = Substitute.For<IActivePullRequestDiscoveryProvider>();
        discoveryProvider.Provider.Returns(provider);
        return discoveryProvider;
    }

    private static ActivePullRequestQuery CreateQuery(ScmProvider provider)
    {
        return new ActivePullRequestQuery(
            provider,
            "https://example.com",
            [new ClaimedRepositoryRef("101", "acme/platform")],
            DateTimeOffset.UtcNow.AddHours(-1),
            Guid.NewGuid());
    }
}
