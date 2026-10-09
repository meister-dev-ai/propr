// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Crawling.Configuration;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using NSubstitute;
using MeisterDev.ProPR.ProCursor.Contracts.Sources;

namespace MeisterDev.ProPR.Application.Tests.Features.Crawling.Configuration;

public sealed class UnknownProviderSelectionCompatibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownScopeSelectionNormalizesPathAndIgnoresSavedScope(bool savedScope)
    {
        var registry = Registry();
        var selected = await new ReviewConfigurationSelectionService(registry).ResolveScopeAsync(
            Guid.NewGuid(), (ScmProvider)999, savedScope ? Guid.NewGuid() : null, " https://provider.example/team ", default);

        Assert.Null(selected.OrganizationScopeId);
        Assert.Equal("https://provider.example/team", selected.OrganizationUrl);
        AssertNoProviderCalls(registry);
    }

    [Theory]
    [InlineData(false, "null")]
    [InlineData(true, "null")]
    [InlineData(false, "empty")]
    [InlineData(true, "empty")]
    [InlineData(false, "populated")]
    [InlineData(true, "populated")]
    public async Task UnknownFilterSelectionRetainsLocalNormalization(bool savedScope, string state)
    {
        var registry = Registry();
        IReadOnlyList<CrawlRepoFilterDto>? filters = state switch
        {
            "null" => null,
            "empty" => [],
            _ =>
            [
                new(
                    Guid.Empty, " repo ", [" main ", "MAIN", " ", "release/*"],
                    new CanonicalSourceReferenceDto(" custom ", " native "), " Display ")
            ],
        };

        var selected = await new ReviewConfigurationSelectionService(registry).ResolveFiltersAsync(
            Guid.NewGuid(), (ScmProvider)999, savedScope ? Guid.NewGuid() : null, "project", filters, default);

        if (state == "populated")
        {
            var filter = Assert.Single(selected);
            Assert.Equal("repo", filter.RepositoryName);
            Assert.Equal("Display", filter.DisplayName);
            Assert.Equal(new CanonicalSourceReferenceDto("custom", "native"), filter.CanonicalSourceRef);
            Assert.Equal(new[] { "main", "release/*" }, filter.TargetBranchPatterns);
        }
        else
        {
            Assert.Empty(selected);
        }

        AssertNoProviderCalls(registry);
    }

    private static IScmProviderRegistry Registry()
    {
        var registry = MeisterDev.ProPR.TestSupport.LocalScmPolicies.CreateRuntimeSubstitute();
        registry.GetReviewSourcePolicy(Arg.Any<ScmProvider>()).Returns(_ => throw new InvalidOperationException("Unknown-family lookup"));
        return registry;
    }

    private static void AssertNoProviderCalls(IScmProviderRegistry registry)
    {
        registry.DidNotReceiveWithAnyArgs().GetReviewSourcePolicy(default);
        registry.DidNotReceiveWithAnyArgs().GetProviderAdminDiscoveryService(default);
        registry.DidNotReceiveWithAnyArgs().IsRegistered(default);
    }
}
