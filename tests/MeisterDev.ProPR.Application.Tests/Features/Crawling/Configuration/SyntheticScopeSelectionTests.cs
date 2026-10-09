// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Configuration;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.TestSupport;
using NSubstitute;

namespace MeisterDev.ProPR.Application.Tests.Features.Crawling.Configuration;

public sealed class SyntheticScopeSelectionTests
{
    private const ScmProvider SyntheticProvider = (ScmProvider)792;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScopePresenceUsesDeclaredSyntheticRequirement(bool required)
    {
        var (registry, _) = Registry(required);
        Assert.Equal(
            required, new ReviewConfigurationSelectionService(registry)
                .HasScopeSelection(SyntheticProvider, Guid.NewGuid(), null));
        AssertNoStrictOrRegistrationLookup(registry);
        registry.DidNotReceiveWithAnyArgs().GetProviderAdminDiscoveryService(default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ScopeSelectionUsesDeclaredSyntheticRequirement(bool required)
    {
        var (registry, policy) = Registry(required);
        var client = Guid.NewGuid();
        var scopeId = Guid.NewGuid();
        var scope = new ClientScmScopeDto(
            scopeId, client, Guid.NewGuid(), "scope", "external", "captured",
            "Scope", "verified", true, null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var discovery = Substitute.For<IProviderAdminDiscoveryService>();
        registry.GetProviderAdminDiscoveryService(SyntheticProvider).Returns(discovery);
        using var cancellation = new CancellationTokenSource();
        discovery.GetScopeAsync(client, scopeId, cancellation.Token).Returns(scope);
        policy.ValidateGuidedSelectedScope(scope).Returns(scope with { ScopePath = "selected" });
        policy.ResolveGuidedScopePath(" raw ", "crawl").Returns("unscoped");

        var result = await new ReviewConfigurationSelectionService(registry).ResolveScopeAsync(client, SyntheticProvider, scopeId, " raw ", cancellation.Token);

        Assert.Equal(required ? scopeId : (Guid?)null, result.OrganizationScopeId);
        Assert.Equal(required ? "selected" : "unscoped", result.OrganizationUrl);
        if (required)
        {
            await discovery.Received(1).GetScopeAsync(client, scopeId, cancellation.Token);
            policy.Received(1).ValidateGuidedSelectedScope(scope);
            policy.DidNotReceiveWithAnyArgs().ResolveGuidedScopePath(default, default!);
        }
        else
        {
            registry.DidNotReceiveWithAnyArgs().GetProviderAdminDiscoveryService(default);
            policy.DidNotReceiveWithAnyArgs().ValidateGuidedSelectedScope(default);
        }

        AssertNoStrictOrRegistrationLookup(registry);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FilterSelectionUsesDeclaredSyntheticRequirement(bool required)
    {
        var (registry, _) = Registry(required);
        var client = Guid.NewGuid();
        var scopeId = Guid.NewGuid();
        var discovery = Substitute.For<IProviderAdminDiscoveryService>();
        registry.GetProviderAdminDiscoveryService(SyntheticProvider).Returns(discovery);
        using var cancellation = new CancellationTokenSource();
        discovery.ListCrawlFilterOptionsAsync(client, scopeId, "project", cancellation.Token)
            .Returns(new[] { new ScmDiscoveryCrawlFilterOption(new("synthetic", "repository"), "Discovered", []) });

        var filters = await new ReviewConfigurationSelectionService(registry).ResolveFiltersAsync(
            client, SyntheticProvider, scopeId, "project",
            [new(Guid.NewGuid(), " repository ", [" main ", "MAIN"], new(" synthetic ", " repository "), null)],
            cancellation.Token);

        var filter = Assert.Single(filters);
        Assert.Equal("repository", filter.RepositoryName);
        Assert.Equal(new[] { "main" }, filter.TargetBranchPatterns);
        Assert.Equal(required ? "Discovered" : null, filter.DisplayName);
        if (required)
        {
            await discovery.Received(1).ListCrawlFilterOptionsAsync(client, scopeId, "project", cancellation.Token);
        }
        else
        {
            registry.DidNotReceiveWithAnyArgs().GetProviderAdminDiscoveryService(default);
        }

        AssertNoStrictOrRegistrationLookup(registry);
    }

    [Fact]
    public async Task DefinedMissingPolicyRetainsStrictErrorBeforePathOrDiscovery()
    {
        var registry = LocalScmPolicies.CreateRuntimeSubstitute();
        var error = new InvalidOperationException("Missing local policy");
        registry.GetReviewSourcePolicy(ScmProvider.GitLab).Returns(_ => throw error);
        var service = new ReviewConfigurationSelectionService(registry);
        var scope = Guid.NewGuid();

        Assert.Same(
            error, Assert.Throws<InvalidOperationException>(() =>
                service.HasScopeSelection(ScmProvider.GitLab, scope, null)));
        Assert.Same(
            error, await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ResolveScopeAsync(Guid.NewGuid(), ScmProvider.GitLab, scope, "valid path", default)));
        Assert.Same(
            error, await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ResolveFiltersAsync(Guid.NewGuid(), ScmProvider.GitLab, scope, "project", [], default)));
        registry.DidNotReceiveWithAnyArgs().GetSourceIdentityPolicy(default);
        registry.DidNotReceiveWithAnyArgs().GetProviderAdminDiscoveryService(default);
        registry.DidNotReceiveWithAnyArgs().IsRegistered(default);
    }

    [Fact]
    public async Task PathAndNullOrUnscopedFiltersRetainTheirShortCircuits()
    {
        var registry = LocalScmPolicies.CreateRuntimeSubstitute();
        registry.GetReviewSourcePolicy(ScmProvider.GitLab).Returns(_ => throw new InvalidOperationException("Missing policy"));
        var service = new ReviewConfigurationSelectionService(registry);
        Assert.True(service.HasScopeSelection(ScmProvider.GitLab, Guid.NewGuid(), "path"));
        Assert.Empty(await service.ResolveFiltersAsync(Guid.NewGuid(), ScmProvider.GitLab, Guid.NewGuid(), "project", null, default));
        Assert.Empty(await service.ResolveFiltersAsync(Guid.NewGuid(), ScmProvider.GitLab, null, "project", [], default));
        registry.DidNotReceiveWithAnyArgs().GetReviewSourcePolicy(default);
        registry.DidNotReceiveWithAnyArgs().GetSourceIdentityPolicy(default);
        registry.DidNotReceiveWithAnyArgs().GetProviderAdminDiscoveryService(default);
        registry.DidNotReceiveWithAnyArgs().IsRegistered(default);
    }

    private static (IScmProviderRegistry Registry, IReviewSourcePolicy Policy) Registry(bool required)
    {
        var registry = LocalScmPolicies.CreateRuntimeSubstitute();
        var policy = Substitute.For<IReviewSourcePolicy>();
        policy.Provider.Returns(SyntheticProvider);
        policy.RequiresOrganizationScope.Returns(required);
        registry.GetSourceIdentityPolicy(SyntheticProvider).Returns(policy);
        return (registry, policy);
    }

    private static void AssertNoStrictOrRegistrationLookup(IScmProviderRegistry registry)
    {
        registry.DidNotReceiveWithAnyArgs().GetReviewSourcePolicy(default);
        registry.DidNotReceiveWithAnyArgs().IsRegistered(default);
    }
}
