// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Configuration;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using NSubstitute;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Discovery;
using MeisterDev.ProPR.ProCursor.Contracts.Sources;


namespace MeisterDev.ProPR.Application.Tests.Features.Crawling.Configuration;

public sealed class ReviewConfigurationSelectionServiceTests
{
    [Fact]
    public void ProviderDiscoveryPortCarriesNeutralInternalOptions()
    {
        var methods = typeof(IProviderAdminDiscoveryService).GetMethods();
        Assert.DoesNotContain(methods, method => ContainsAdoDto(method.ReturnType));
        Assert.False(ContainsAdoDto(typeof(GuidedSourceSelection).GetProperty("Source")!.PropertyType));
    }

    [Fact]
    public void LocalGuidedValidationBelongsToTheSourcePolicy()
    {
        var policy = MeisterDev.ProPR.TestSupport.LocalScmPolicies.Registry.GetReviewSourcePolicy(ScmProvider.AzureDevOps);
        Assert.Equal("https://dev.azure.com/org", policy.ResolveGuidedScopePath(" https://dev.azure.com/org ", "crawl"));
        Assert.Equal(
            "The selected Azure DevOps organization is disabled.",
            Assert.Throws<InvalidOperationException>(() => policy.ValidateGuidedSelectedScope(Scope() with { IsEnabled = false })).Message);
    }

    [Fact]
    public void NativeGuidedSourceSelectionUsesTheExistingDiscoveryPort()
    {
        Assert.NotNull(typeof(IProviderAdminDiscoveryService).GetMethod("ResolveGuidedSourceAsync"));
    }

    private static bool ContainsAdoDto(Type type) =>
        type.Namespace == "MeisterDev.ProPR.Application.DTOs.AzureDevOps"
        || type.IsGenericType && type.GetGenericArguments().Any(ContainsAdoDto);

    [Fact]
    public async Task SavedPathSelectionUsesLocalNativeProjectionWithoutDiscovery()
    {
        var registry = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
        var policy = Substitute.For<IReviewSourcePolicy>();
        policy.ResolveGuidedScopePath(" raw path ", "crawl").Returns("native path");
        registry.GetSourceIdentityPolicy(ScmProvider.GitLab).Returns(policy);

        var result = await new ReviewConfigurationSelectionService(registry)
            .ResolveScopeAsync(Guid.NewGuid(), ScmProvider.GitLab, null, " raw path ", default);

        Assert.Equal("native path", result.OrganizationUrl);
        registry.DidNotReceiveWithAnyArgs().GetProviderAdminDiscoveryService(default);
    }

    [Fact]
    public async Task GuidedSourceCoordinationDelegatesNativeSelectionWithoutExtraDiscovery()
    {
        var registry = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
        var discovery = Substitute.For<IProviderAdminDiscoveryService>();
        registry.GetProviderAdminDiscoveryService(ScmProvider.AzureDevOps).Returns(discovery);
        var scope = Scope();
        var expected = new GuidedSourceSelection(
            scope,
            new("Repository", new("azureDevOps", "repo"), "Repository", "main"), ["main"]);
        discovery.ResolveGuidedSourceAsync(
            scope.ClientId, scope.Id, "project", ProCursorSourceKind.Repository,
            new CanonicalSourceReferenceDto("azureDevOps", "repo"), Arg.Any<CancellationToken>()).Returns(expected);

        var result = await new ReviewConfigurationSelectionService(registry).ResolveGuidedSourceAsync(
            scope.ClientId, ScmProvider.AzureDevOps, scope.Id, "project", ProCursorSourceKind.Repository, new("azureDevOps", "repo"), default);

        Assert.Same(expected, result);
        await discovery.DidNotReceiveWithAnyArgs().GetScopeAsync(default, default);
        await discovery.DidNotReceiveWithAnyArgs().ListSourceOptionsAsync(default, default, default!, default);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("disabled")]
    [InlineData("empty")]
    public async Task GuidedSourceSelectionPreservesScopeErrorsBeforeNullReferenceEvaluation(string state)
    {
        var registry = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
        var discovery = Substitute.For<IProviderAdminDiscoveryService>();
        registry.GetProviderAdminDiscoveryService(ScmProvider.AzureDevOps).Returns(discovery);
        var scope = Scope();
        discovery.GetScopeAsync(scope.ClientId, scope.Id, Arg.Any<CancellationToken>())
            .Returns(state == "missing" ? null : scope with { IsEnabled = state != "disabled" });
        discovery.ListSourceOptionsAsync(
            scope.ClientId, scope.Id, "project", ProCursorSourceKind.Repository,
            Arg.Any<CancellationToken>()).Returns(Array.Empty<ScmDiscoverySourceOption>());
        discovery.ResolveGuidedSourceAsync(
                scope.ClientId, scope.Id, "project", ProCursorSourceKind.Repository,
                Arg.Any<CanonicalSourceReferenceDto?>(), Arg.Any<CancellationToken>())
            .Returns(call => AdoGuidedDiscovery.ResolveSourceAsync(
                discovery, scope.ClientId, scope.Id, "project",
                ProCursorSourceKind.Repository, call.ArgAt<CanonicalSourceReferenceDto?>(4), call.ArgAt<CancellationToken>(5)));
        var action = () => new ReviewConfigurationSelectionService(registry).ResolveGuidedSourceAsync(
            scope.ClientId, ScmProvider.AzureDevOps, scope.Id, "project", ProCursorSourceKind.Repository, null!, default);

        if (state == "missing")
        {
            Assert.Equal(
                $"Organization scope {scope.Id} was not found for client {scope.ClientId}.",
                (await Assert.ThrowsAsync<KeyNotFoundException>(action)).Message);
        }
        else
        {
            Assert.Equal(
                state == "disabled"
                    ? "The selected organization scope is disabled."
                    : "The selected source is no longer available in Azure DevOps.",
                (await Assert.ThrowsAsync<InvalidOperationException>(action)).Message);
        }

        await discovery.Received(1).GetScopeAsync(scope.ClientId, scope.Id, Arg.Any<CancellationToken>());
        await discovery.DidNotReceiveWithAnyArgs().ListBranchOptionsAsync(default, default, default!, default, default!);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ScopeIdSelectionUsesNativeRequirement(bool requiresScope, bool expected)
    {
        var registry = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
        var policy = Substitute.For<IReviewSourcePolicy>();
        policy.RequiresOrganizationScope.Returns(requiresScope);
        registry.GetReviewSourcePolicy(ScmProvider.GitLab).Returns(policy);
        Assert.Equal(expected, new ReviewConfigurationSelectionService(registry).HasScopeSelection(ScmProvider.GitLab, Guid.NewGuid(), null));
    }

    [Fact]
    public async Task NullFiltersDoNotResolveNativeCapabilitiesOrCallDiscovery()
    {
        var registry = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
        Assert.Empty(
            await new ReviewConfigurationSelectionService(registry).ResolveFiltersAsync(
                Guid.NewGuid(), ScmProvider.AzureDevOps, Guid.NewGuid(), "project", null, default));
        registry.DidNotReceiveWithAnyArgs().GetReviewSourcePolicy(default);
        registry.DidNotReceiveWithAnyArgs().GetProviderAdminDiscoveryService(default);
    }

    [Fact]
    public async Task SelectedDisabledScopeIsRejectedBeforeFilterDiscovery()
    {
        var registry = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
        var policy = Substitute.For<IReviewSourcePolicy>();
        policy.RequiresOrganizationScope.Returns(true);
        registry.GetReviewSourcePolicy(ScmProvider.AzureDevOps).Returns(policy);
        var discovery = Substitute.For<IProviderAdminDiscoveryService>();
        registry.GetProviderAdminDiscoveryService(ScmProvider.AzureDevOps).Returns(discovery);
        var scope = Scope() with { IsEnabled = false };
        discovery.GetScopeAsync(scope.ClientId, scope.Id, Arg.Any<CancellationToken>()).Returns(scope);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ReviewConfigurationSelectionService(registry).ResolveScopeAsync(scope.ClientId, ScmProvider.AzureDevOps, scope.Id, null, default));
        Assert.Equal("The selected Azure DevOps organization is disabled.", error.Message);
        await discovery.DidNotReceiveWithAnyArgs().ListCrawlFilterOptionsAsync(default, default, default!);
    }

    [Fact]
    public async Task GuidedSourceSelectionRetainsReferenceMatchingAndBranchNormalization()
    {
        var registry = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
        var discovery = Substitute.For<IProviderAdminDiscoveryService>();
        registry.GetProviderAdminDiscoveryService(ScmProvider.AzureDevOps).Returns(discovery);
        var scope = Scope();
        var source = new ScmDiscoverySourceOption("repository", new CanonicalSourceReferenceDto("azureDevOps", "REPOSITORY"), "Repository", "main");
        discovery.GetScopeAsync(scope.ClientId, scope.Id, Arg.Any<CancellationToken>()).Returns(scope);
        discovery.ListSourceOptionsAsync(scope.ClientId, scope.Id, "project", ProCursorSourceKind.Repository, Arg.Any<CancellationToken>())
            .Returns(new[] { source });
        discovery.ListBranchOptionsAsync(
                scope.ClientId, scope.Id, "project", ProCursorSourceKind.Repository, Arg.Any<CanonicalSourceReferenceDto>(), Arg.Any<CancellationToken>())
            .Returns(
                new[] { new ScmDiscoveryBranchOption("main", true), new ScmDiscoveryBranchOption("MAIN", false), new ScmDiscoveryBranchOption(" ", false) });
        discovery.ResolveGuidedSourceAsync(
                scope.ClientId, scope.Id, "project", ProCursorSourceKind.Repository,
                Arg.Any<CanonicalSourceReferenceDto>(), Arg.Any<CancellationToken>())
            .Returns(call => AdoGuidedDiscovery.ResolveSourceAsync(
                discovery, scope.ClientId, scope.Id, "project",
                ProCursorSourceKind.Repository, call.ArgAt<CanonicalSourceReferenceDto>(4), call.ArgAt<CancellationToken>(5)));
        var result = await new ReviewConfigurationSelectionService(registry).ResolveGuidedSourceAsync(
            scope.ClientId, ScmProvider.AzureDevOps, scope.Id, "project", ProCursorSourceKind.Repository,
            new CanonicalSourceReferenceDto("AzureDevOps", "repository"), default);
        Assert.Same(source, result.Source);
        Assert.Equal(new[] { "main" }, result.Branches);
        registry.DidNotReceive().IsRegistered(Arg.Any<ScmProvider>());
    }

    private static ClientScmScopeDto Scope() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "organization", "org", "https://dev.azure.com/org", "Organization", "verified", true, null, null,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
}
