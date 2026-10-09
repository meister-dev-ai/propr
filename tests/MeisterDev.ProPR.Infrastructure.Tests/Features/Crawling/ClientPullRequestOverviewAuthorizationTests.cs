// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Configuration;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Crawling.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Crawling;

public sealed class ClientPullRequestOverviewAuthorizationTests
{
    [Theory]
    [InlineData(ScmProvider.AzureDevOps, false)]
    [InlineData(ScmProvider.AzureDevOps, true)]
    [InlineData(ScmProvider.GitHub, false)]
    [InlineData(ScmProvider.GitHub, true)]
    [InlineData(ScmProvider.GitLab, false)]
    [InlineData(ScmProvider.GitLab, true)]
    [InlineData(ScmProvider.Forgejo, false)]
    [InlineData(ScmProvider.Forgejo, true)]
    public async Task MissingSourcePolicyDeniesInitialAndRetainedCursorRequestsBeforeCacheOrProviderReads(ScmProvider provider, bool retainedCursor)
    {
        var setup = new AuthorizationSetup(provider, retainedCursor);
        setup.Providers.GetReviewSourcePolicy(provider)
            .Returns(_ => throw new InvalidOperationException("The provider review-source policy is unavailable."));

        var exception = await Assert.ThrowsAsync<ClientPullRequestOverviewException>(() => setup.Service.ReadAsync(
            setup.ClientId, setup.Request, CancellationToken.None));

        Assert.Equal("accessDenied", exception.Kind);
        setup.AssertNoCacheOrProviderReads();
    }

    [Fact]
    public async Task SourcePolicyExecutionFailureIsNotNormalizedAsMissingPolicy()
    {
        var setup = new AuthorizationSetup(ScmProvider.GitHub, false);
        var policy = Substitute.For<IReviewSourcePolicy>();
        var failure = new InvalidOperationException("The source policy failed.");
        setup.Providers.GetReviewSourcePolicy(ScmProvider.GitHub).Returns(policy);
        policy.IsCacheScopeCompatible(Arg.Any<string>(), Arg.Any<string>()).Returns(_ => throw failure);

        var exception =
            await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Service.ReadAsync(setup.ClientId, setup.Request, CancellationToken.None));

        Assert.Same(failure, exception);
        setup.AssertNoCacheOrProviderReads();
    }

    [Fact]
    public async Task OtherRegistryFailureIsNotNormalizedAsMissingPolicy()
    {
        var setup = new AuthorizationSetup(ScmProvider.GitHub, false);
        var failure = new NotSupportedException("The registry failed.");
        setup.Providers.GetReviewSourcePolicy(ScmProvider.GitHub).Returns(_ => throw failure);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => setup.Service.ReadAsync(setup.ClientId, setup.Request, CancellationToken.None));

        Assert.Same(failure, exception);
        setup.AssertNoCacheOrProviderReads();
    }

    private sealed class AuthorizationSetup
    {
        private readonly IClientPullRequestOverviewStore store = Substitute.For<IClientPullRequestOverviewStore>();
        private readonly IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        private readonly IReviewDiscoveryProvider discovery = Substitute.For<IReviewDiscoveryProvider>();
        private readonly IReviewOverviewProvider overview = Substitute.For<IReviewOverviewProvider>();

        public AuthorizationSetup(ScmProvider provider, bool retainedCursor)
        {
            var targetId = Guid.NewGuid();
            var connectionId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            var clients = Substitute.For<IClientRegistry>();
            clients.GetTenantIdAsync(ClientId, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
            var configurations = Substitute.For<ICrawlConfigurationRepository>();
            configurations.GetReviewTargetPolicySnapshotAsync(targetId, ClientId, Arg.Any<CancellationToken>())
                .Returns(
                    new CrawlConfigurationDto(
                        targetId, ClientId, provider, "https://scm.example", "team", 60, false, now,
                        [new(Guid.NewGuid(), "repo", [], new(provider.ToString(), "repo-1"))]));
            var connections = Substitute.For<IClientScmConnectionRepository>();
            connections.GetByIdAsync(ClientId, connectionId, Arg.Any<CancellationToken>())
                .Returns(
                    new ClientScmConnectionDto(
                        connectionId, ClientId, provider, "https://scm.example", ScmAuthenticationKind.PersonalAccessToken,
                        "SCM", true, "verified", now, null, null, now, now));
            var scopes = Substitute.For<IClientScmScopeRepository>();
            scopes.GetByConnectionIdAsync(ClientId, connectionId, Arg.Any<CancellationToken>())
                .Returns(
                    new[]
                    {
                        new ClientScmScopeDto(
                            Guid.NewGuid(), ClientId, connectionId, "repository", "repo-1", "team/repo", "repo",
                            "verified", true, now, null, now, now)
                    });
            Providers.GetReviewDiscoveryProvider(provider).Returns(discovery);
            Providers.GetReviewOverviewProvider(provider).Returns(overview);
            var protection = new EphemeralDataProtectionProvider();
            var cursor = retainedCursor
                ? protection.CreateProtector("ProPR.ClientPullRequestOverview.Cursor.v1").Protect(Guid.NewGuid().ToString("N"))
                : null;
            Request = new([new(targetId, connectionId)], "binding", cursor, Page: retainedCursor ? 2 : 1);
            Service = new ClientPullRequestOverviewService(
                store, clients, configurations, connections, scopes, Providers, scopeFactory, protection, TimeProvider.System);
        }

        public Guid ClientId { get; } = Guid.NewGuid();
        public IScmProviderRegistry Providers { get; } = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
        public ClientPullRequestOverviewRequest Request { get; }
        public ClientPullRequestOverviewService Service { get; }

        public void AssertNoCacheOrProviderReads()
        {
            Assert.Empty(store.ReceivedCalls());
            Assert.Empty(scopeFactory.ReceivedCalls());
            Assert.Empty(discovery.ReceivedCalls());
            Assert.Empty(overview.ReceivedCalls());
            Assert.All(Providers.ReceivedCalls(), call => Assert.Equal(nameof(IScmProviderRegistry.GetReviewSourcePolicy), call.GetMethodInfo().Name));
        }
    }
}
