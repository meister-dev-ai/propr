// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Common;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using MeisterDev.ProPR.Infrastructure.Features.Clients;
using MeisterDev.ProPR.Infrastructure.Features.Crawling;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Reviewing.DependencyInjection;
using MeisterDev.ProPR.ProCursor.Contracts.Sources;

namespace MeisterDev.ProPR.Infrastructure.Tests.Providers;

public sealed class ReviewSourcePolicyTests
{
    [Fact]
    public void MentionScopeSelectionDeclaresNativeSavedCoverageWithoutLiveProviders()
    {
        var selection = Resolve(ScmProvider.AzureDevOps).PrepareMentionScopeSelection(" https://DEV.azure.com/org/ ");
        Assert.Equal("organization", selection.ScopeType);
        Assert.True(selection.MatchesScope("https://dev.azure.com/org"));
        Assert.False(selection.MatchesScope("https://dev.azure.com/other"));
    }

    [Theory]
    [InlineData("clients")]
    [InlineData("reviewing")]
    [InlineData("crawling")]
    [InlineData("offline")]
    public void StandaloneModulesExposePurePoliciesWithoutDatabaseConfiguration(string module)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        switch (module)
        {
            case "clients":
                services.AddClientsModule(configuration);
                break;
            case "reviewing":
                services.AddReviewingModule(configuration);
                break;
            case "crawling":
                services.AddCrawlingModule(configuration);
                break;
            default:
                services.AddReviewEvalHarness(configuration);
                break;
        }

        using var host = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        Assert.Equal(4, host.GetServices<IReviewSourcePolicy>().Count());
        Assert.Equal(4, host.GetServices<IScmConnectionConfigurationPolicy>().Count());
        Assert.Equal(4, host.GetServices<IScmIdentityPolicy>().Count());
        Assert.Equal(4, host.GetServices<ICodeReviewPreparationPolicy>().Count());
        Assert.Equal(4, host.GetServices<IWebhookIngressPolicy>().Count());
        Assert.All(
            services.Where(service => service.ServiceType == typeof(IWebhookIngressPolicy)),
            service => Assert.Equal(ServiceLifetime.Singleton, service.Lifetime));
        Assert.All(
            services.Where(service => service.ServiceType == typeof(IScmConnectionConfigurationPolicy)),
            service => Assert.Equal(ServiceLifetime.Singleton, service.Lifetime));
        if (module == "offline")
        {
            Assert.False(host.GetRequiredService<IScmProviderRegistry>().IsRegistered(ScmProvider.AzureDevOps));
        }
    }

    [Theory]
    [InlineData(ScmProvider.AzureDevOps, "Team", "Team/repo", "Team")]
    [InlineData(ScmProvider.GitHub, "Team", "Team/repo", "Team/repo")]
    [InlineData(ScmProvider.GitLab, "Group/Nested", "Group/Nested/repo", "Group/Nested/repo")]
    [InlineData(ScmProvider.Forgejo, "Team", "Team/repo", "Team/repo")]
    public void LocalPolicy_ConstructsQualifiedRepositoryNamesWithoutDuplicatingTheOwner(ScmProvider provider, string project, string name, string expectedPath)
    {
        var repository = Resolve(provider).CreateCapturedRepository("https://host.test", "42", project, name);
        Assert.Equal(expectedPath, repository.ProjectPath);
        Assert.Equal("repo", repository.RepositoryName);
    }

    [Theory]
    [InlineData(ScmProvider.AzureDevOps, "Team", "42", "owner/repo", "other/repo", "Team")]
    [InlineData(ScmProvider.GitHub, "owner", "42", "owner/repo", "other/repo", "owner/repo")]
    [InlineData(ScmProvider.GitLab, "group", "42", "group/nested/repo", "group/nested/repo", "42")]
    [InlineData(ScmProvider.Forgejo, "owner", "42", null, "other/repo", "other/repo")]
    public void CapturedMentionProjectionPreservesTheExistingNameGrammar(
        ScmProvider provider, string project, string id, string? claimedName, string pullRequestName, string expected)
    {
        Assert.Equal(expected, Resolve(provider).ResolveMentionRepositoryPath(project, id, claimedName, pullRequestName));
    }

    [Fact]
    public void Registry_DeclaresLocalReviewPreparationFacet()
    {
        var registry = MeisterDev.ProPR.Infrastructure.Features.Providers.Common.DependencyInjection.ScmLocalPolicyFactory.CreateRegistry();
        foreach (var provider in Enum.GetValues<ScmProvider>())
        {
            Assert.Equal(provider, registry.GetCodeReviewPreparationPolicy(provider).Provider);
            Assert.False(registry.IsRegistered(provider));
        }
    }

    [Fact]
    public void Registry_RejectsDuplicateNativePolicyRegistration()
    {
        var source = new MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Support.GitHubReviewSourcePolicy();
        Assert.Throws<ArgumentException>(() => new ScmProviderRegistry(
            [], [], [], [], [], [], [], [], [], [], [], [],
            reviewSourcePolicies: [source, source]));
    }

    [Fact]
    public void Registry_DeclaresCredentialFreeIdentityFacet()
    {
        var registry = MeisterDev.ProPR.Infrastructure.Features.Providers.Common.DependencyInjection.ScmLocalPolicyFactory.CreateRegistry();
        foreach (var provider in Enum.GetValues<ScmProvider>())
        {
            Assert.Equal(provider, registry.GetIdentityPolicy(provider).Provider);
            Assert.Equal(provider, registry.GetReviewSourcePolicy(provider).Provider);
            Assert.False(registry.IsRegistered(provider));
            Assert.Empty(registry.GetRegisteredCapabilities(provider));
            Assert.Throws<InvalidOperationException>(() => registry.GetCodeReviewQueryService(provider));
        }

        var unknown = (ScmProvider)99;
        Assert.Equal("https://host.test", registry.GetSourceIdentityPolicy(unknown).NormalizeNamespace("https://user:pass@host.test/path"));
        Assert.Throws<InvalidOperationException>(() => registry.GetReviewSourcePolicy(unknown));
        Assert.Equal(
            MeisterDev.ProPR.Application.Features.ThreadOwnership.ProviderCommentIdScope.Thread,
            registry.GetIdentityPolicy(unknown).CommentIdScope);
        Assert.False(registry.IsRegistered(unknown));
    }

    [Theory]
    [InlineData(ScmProvider.AzureDevOps, "42", "Project", "repo", "42")]
    [InlineData(ScmProvider.GitHub, "42", "owner", "owner/repo", "owner/repo")]
    [InlineData(ScmProvider.GitLab, "42", "group/nested", "group/nested/repo", "group/nested/repo")]
    [InlineData(ScmProvider.Forgejo, "repo", "owner", null, "owner/repo")]
    public void LocalPolicy_PreservesSavedRepositoryComparisonKey(ScmProvider provider, string id, string project, string? path, string expected)
    {
        var policy = Resolve(provider);
        Assert.Equal(expected, policy.GetRepositoryIdentityKey(id, id, project, path, null));
    }

    [Theory]
    [InlineData(ScmProvider.AzureDevOps, "https://user:pass@dev.azure.com/team?x=y#fragment", "https://dev.azure.com")]
    [InlineData(ScmProvider.AzureDevOps, "https://user:pass@ado.test/tfs/collection?x=y#fragment", "https://ado.test/tfs/collection")]
    [InlineData(ScmProvider.GitHub, "https://host.test/base/path?x=y#fragment", "https://host.test")]
    [InlineData(ScmProvider.GitLab, "https://host.test/base/path", "https://host.test")]
    [InlineData(ScmProvider.Forgejo, "https://host.test/base/path", "https://host.test")]
    public void LocalPolicy_ProjectsCapturedNamespaceWithoutLiveAdapters(ScmProvider provider, string source, string expected)
    {
        var policy = Resolve(provider);
        Assert.Equal(expected, policy.NormalizeNamespace(source));
    }

    [Theory]
    [InlineData(ScmProvider.AzureDevOps, "https://ado.test/collection", "https://ado.test", "https://ado.test/collection")]
    [InlineData(ScmProvider.GitHub, "https://host.test/group", "https://host.test/base", "https://host.test/base")]
    [InlineData(ScmProvider.GitLab, "https://host.test/group", null, "https://host.test/group")]
    [InlineData(ScmProvider.Forgejo, "https://host.test/group", "https://host.test", "https://host.test")]
    public void LocalPolicy_SelectsCapturedSourceWithoutLiveAdapters(ScmProvider provider, string scope, string? host, string expected)
    {
        var policy = Resolve(provider);
        Assert.Equal(expected, policy.SelectCapturedSource(scope, host));
    }

    [Theory]
    [InlineData(ScmProvider.AzureDevOps, "https://dev.azure.com", "https://dev.azure.com/team", true)]
    [InlineData(ScmProvider.AzureDevOps, "https://ado.test/TFS", "https://ado.test/tfs", true)]
    [InlineData(ScmProvider.AzureDevOps, "https://dev.azure.com", "https://dev.azure.com", false)]
    [InlineData(ScmProvider.GitHub, "https://host.test/base", "https://host.test/base", true)]
    [InlineData(ScmProvider.GitLab, "https://host.test/base", "https://host.test/Base", false)]
    [InlineData(ScmProvider.Forgejo, "https://host.test/base", "https://host.test/other", false)]
    public void RegisteredPolicy_ValidatesSelectedDeploymentScope(ScmProvider provider, string host, string scope, bool expected)
    {
        var policy = Resolve(provider);
        Assert.Equal(expected, policy.IsSelectedScopeCompatible(host, scope));
    }

    [Theory]
    [InlineData(ScmProvider.AzureDevOps, "https://dev.azure.com/Team", "Project", "/team", "project", "Project")]
    [InlineData(ScmProvider.GitHub, "https://host.test/Base", "Team", "/Base", "Team", "Team/repo")]
    [InlineData(ScmProvider.GitLab, "https://host.test/Base", "Group/Nested", "/Base", "Group/Nested", "Group/Nested/repo")]
    [InlineData(ScmProvider.Forgejo, "https://host.test/Base", "Team", "/Base", "Team", "Team/repo")]
    public void RegisteredPolicy_PreservesCacheAndRepositoryCoordinates(
        ScmProvider provider, string scope, string project, string expectedPath, string expectedProject, string repositoryPath)
    {
        var policy = Resolve(provider);
        var coordinates = policy.GetCacheCoordinates(scope, project);
        Assert.Equal(expectedPath, coordinates.ScopePath);
        Assert.Equal(expectedProject, coordinates.ProjectKey);
        Assert.Equal(repositoryPath, policy.CreateRepository(scope, "42", project, "repo").ProjectPath);
    }

    [Theory]
    [InlineData(ScmProvider.AzureDevOps, "organization", "ignored", "https://host.test/Team/", true)]
    [InlineData(ScmProvider.AzureDevOps, "Organization", "ignored", "https://host.test/Team/", false)]
    [InlineData(ScmProvider.GitHub, "organization", "ignored", "https://host.test/Team/", false)]
    [InlineData(ScmProvider.GitHub, "repository", "native", "/Team/repo/", true)]
    [InlineData(ScmProvider.GitLab, "repository", "native", "/team/REPO/", true)]
    [InlineData(ScmProvider.Forgejo, "repository", "NATIVE", "/Team/repo/", false)]
    public void RegisteredPolicy_PreservesGrantTypeAndIdentityComparisons(ScmProvider provider, string scopeType, string externalId, string path, bool expected)
    {
        var policy = Resolve(provider);
        var target = Target(provider);
        var now = DateTimeOffset.UtcNow;
        var grant = new ClientScmScopeDto(
            Guid.NewGuid(), target.ClientId, Guid.NewGuid(), scopeType, externalId, path,
            "Fixture", "verified", true, null, null, now, now);
        Assert.Equal(expected, policy.MatchesGrant(target, grant));
    }

    [Theory]
    [InlineData(ScmProvider.AzureDevOps, "different", false)]
    [InlineData(ScmProvider.GitHub, "different", true)]
    [InlineData(ScmProvider.GitLab, "different", true)]
    [InlineData(ScmProvider.Forgejo, "different", true)]
    [InlineData(ScmProvider.AzureDevOps, "NATIVE", true)]
    public void RegisteredPolicy_MatchesOnlySupportedRepositoryAliases(ScmProvider provider, string externalId, bool expected)
    {
        var policy = Resolve(provider);
        var repository = policy.CreateRepository("https://host.test", externalId, "Team", "repo");
        Assert.Equal(expected, policy.MatchesRepository(Target(provider), repository));
    }

    private static CrawlConfigurationDto Target(ScmProvider provider) =>
        new(
            Guid.NewGuid(), Guid.NewGuid(), provider, "https://host.test/team", "Team", 60, false, DateTimeOffset.UtcNow,
            [new CrawlRepoFilterDto(Guid.NewGuid(), "repo", [], new CanonicalSourceReferenceDto(provider.ToString(), "native"))]);

    private static IReviewSourcePolicy Resolve(ScmProvider provider)
    {
        var services = new ServiceCollection();
        services.AddAzureDevOpsProviderAdapters();
        services.AddGitHubProviderAdapters();
        services.AddGitLabProviderAdapters();
        services.AddForgejoProviderAdapters();
        services.AddSingleton<IEnumerable<IRepositoryDiscoveryProvider>>([]);
        services.AddSingleton<IEnumerable<IActivePullRequestDiscoveryProvider>>([]);
        services.AddSingleton<IEnumerable<ICodeReviewQueryService>>([]);
        services.AddSingleton<IEnumerable<ICodeReviewPublicationService>>([]);
        services.AddSingleton<IEnumerable<IReviewDiscoveryProvider>>([]);
        services.AddSingleton<IEnumerable<IReviewerIdentityService>>([]);
        services.AddSingleton<IEnumerable<IReviewAssignmentService>>([]);
        services.AddSingleton<IEnumerable<IReviewThreadStatusWriter>>([]);
        services.AddSingleton<IEnumerable<IReviewThreadReplyPublisher>>([]);
        services.AddSingleton<IEnumerable<IProviderAdminDiscoveryService>>([]);
        services.AddSingleton<IEnumerable<IWebhookIngressService>>([]);
        services.AddSingleton<IEnumerable<ILinkedItemProvider>>([]);
        services.AddSingleton<IEnumerable<IReviewOverviewProvider>>([]);
        services.AddSingleton<IScmProviderRegistry, ScmProviderRegistry>();
        using var container = services.BuildServiceProvider();
        return container.GetRequiredService<IScmProviderRegistry>().GetReviewSourcePolicy(provider);
    }
}
