// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Dtos;
using MeisterDev.ProPR.Application.Features.Reviewing.Intake.Queries.ResolvePullRequest;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Tests.Features.Providers;

public sealed class WebhookCoverageCodecTests
{
    [Theory]
    [InlineData(WebhookProviderType.AzureDevOps, ScmProvider.AzureDevOps)]
    [InlineData(WebhookProviderType.GitHub, ScmProvider.GitHub)]
    [InlineData(WebhookProviderType.GitLab, ScmProvider.GitLab)]
    [InlineData(WebhookProviderType.Forgejo, ScmProvider.Forgejo)]
    [InlineData((WebhookProviderType)999, ScmProvider.AzureDevOps)]
    public void CoveragePreservesKnownMappingsAndUndefinedCompatibilityFallback(WebhookProviderType provider, ScmProvider expected)
    {
        var configuration = new WebhookConfigurationDto(
            Guid.NewGuid(), Guid.NewGuid(), provider,
            "path", "https://host.example/scope", "project", true, DateTimeOffset.UtcNow, [], []);

        var coverage = PullRequestCoverage.FromWebhookConfiguration(
            configuration, MeisterDev.ProPR.TestSupport.LocalScmPolicies.Registry.CompatibilityCodec.ResolveCoverageProvider(provider));

        Assert.Equal(expected, coverage.Provider);
        Assert.Equal(configuration.OrganizationUrl, coverage.ProviderScopePath);
        Assert.Equal(configuration.ProjectId, coverage.ProviderProjectKey);
        Assert.Equal(configuration.ClientId, coverage.ClientId);
    }
}
