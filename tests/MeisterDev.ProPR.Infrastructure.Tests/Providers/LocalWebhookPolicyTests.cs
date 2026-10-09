// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.TestSupport;

namespace MeisterDev.ProPR.Infrastructure.Tests.Providers;

public sealed class LocalWebhookPolicyTests
{
    [Theory]
    [InlineData(ScmProvider.AzureDevOps, null, null)]
    [InlineData(ScmProvider.GitHub, "pull_request", " delivery ")]
    [InlineData(ScmProvider.GitLab, "merge_request", " delivery ")]
    [InlineData(ScmProvider.Forgejo, "pull_request", " delivery ")]
    public void LocalIngressMetadataPreservesFirstCaseInsensitiveHeaderAndRawValue(ScmProvider provider, string? expectedEvent, string? expectedDelivery)
    {
        var registry = LocalScmPolicies.Registry;
        var policy = registry.GetWebhookIngressPolicy(provider);
        var headers = new Dictionary<string, string>
        {
            ["x-github-event"] = "pull_request", ["X-GitHub-Event"] = "ignored",
            ["x-github-delivery"] = " delivery ",
            ["x-gitlab-event"] = "merge_request", ["x-gitlab-event-uuid"] = " delivery ",
            ["x-gitea-event"] = "pull_request", ["x-gitea-delivery"] = " delivery ",
        };
        Assert.Equal(expectedEvent, policy.ReadEventType(headers));
        Assert.Equal(expectedDelivery, policy.ReadDeliveryKey(headers));
        Assert.False(registry.IsRegistered(provider));
        Assert.Throws<InvalidOperationException>(() => registry.GetWebhookIngressService(provider));
    }

    [Fact]
    public void ConflictingLocalIngressDeclarationsRemainRejected()
    {
        var policy = LocalScmPolicies.Registry.GetWebhookIngressPolicy(ScmProvider.GitHub);
        Assert.Throws<ArgumentException>(() => new MeisterDev.ProPR.Infrastructure.Features.Providers.Common.ScmProviderRegistry(
            [], [], [], [], [], [], [], [], [], [], [], [], webhookIngressPolicies: [policy, policy]));
    }

    [Theory]
    [InlineData(ScmProvider.AzureDevOps)]
    [InlineData(ScmProvider.GitHub)]
    public void RecordedAzureRepositoryProjectionPreservesCurrentHostFamily(ScmProvider currentProvider)
    {
        var host = new ProviderHostRef(currentProvider, "https://scm.example");
        var repository = new RepositoryRef(host, "display", "display", "display");
        var projected = LocalScmPolicies.Registry.GetReviewSourcePolicy(ScmProvider.AzureDevOps)
            .ProjectRecordedCanonicalRepository(repository, "canonical", "project");
        Assert.Equal(currentProvider, projected.Host.Provider);
        Assert.Equal("canonical", projected.ExternalRepositoryId);
        Assert.Equal("project", projected.ProjectPath);
        Assert.Equal("project", projected.OwnerOrNamespace);
    }

    [Fact]
    public void NativeWebhookRepositoryAliasesPreserveIdentifierPathAndBasenameOrder()
    {
        var repository = new RepositoryRef(
            new ProviderHostRef(ScmProvider.GitHub, "https://scm.example"),
            "42", "owner", "owner/repo");
        var aliases = LocalScmPolicies.Registry.GetReviewSourcePolicy(ScmProvider.GitHub).GetWebhookRepositoryAliases(repository);
        Assert.Equal(["42", "owner/repo", "repo"], aliases);
    }

    [Fact]
    public void NativeReviewerComparisonPreservesLoginAndExternalIdCasingRules()
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://scm.example");
        var expected = new ReviewerIdentity(host, "Id", "reviewer", "Reviewer", true);
        var policy = LocalScmPolicies.Registry.GetIdentityPolicy(host.Provider);
        Assert.True(policy.MatchesReviewerIdentity(new ReviewerIdentity(host, "other", "REVIEWER", "Reviewer", true), expected));
        Assert.False(policy.MatchesReviewerIdentity(new ReviewerIdentity(host, "id", "other", "Other", true), expected));
        Assert.True(policy.MatchesReviewerIdentity(new ReviewerIdentity(host, "Id", "other", "Other", true), expected));
    }
}
