// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Application.Features.Providers.Identity;

namespace MeisterDev.ProPR.Application.Tests.Features.Crawling.Execution;

public sealed class ProviderSourceIdentityTests
{
    [Theory]
    [InlineData(
        ScmProvider.AzureDevOps, "https://user:password@dev.azure.com/org/?token=secret#fragment", "AzureDevOps:https://dev.azure.com",
        "AzureDevOps:https://dev.azure.com/org")]
    [InlineData(
        ScmProvider.AzureDevOps, "https://org.visualstudio.com/project/", "AzureDevOps:https://org.visualstudio.com",
        "AzureDevOps:https://org.visualstudio.com/project")]
    [InlineData(
        ScmProvider.AzureDevOps, "https://ado.example/tfs/Collection%20One/", "AzureDevOps:https://ado.example/tfs/Collection%20One",
        "AzureDevOps:https://ado.example/tfs/Collection%20One")]
    [InlineData(
        ScmProvider.GitHub, "https://user:password@github.example/base/path/?token=secret#fragment", "GitHub:https://github.example",
        "GitHub:https://github.example/base/path")]
    [InlineData(ScmProvider.GitLab, "https://gitlab.example/base/", "GitLab:https://gitlab.example", "GitLab:https://gitlab.example/base")]
    [InlineData(ScmProvider.Forgejo, "https://forgejo.example/base/", "Forgejo:https://forgejo.example", "Forgejo:https://forgejo.example/base")]
    [InlineData(ScmProvider.AzureDevOps, "invalid", "", "")]
    public void SourceAndConfiguredHostRetainTheirDistinctNamespaces(ScmProvider provider, string input, string source, string configured)
    {
        Assert.Equal(source, ProviderSourceIdentity.FromReviewSource(provider, input).Value);
        Assert.Equal(configured, ProviderSourceIdentity.FromConfiguredHost(provider, input).Value);
    }

    [Fact]
    public void ReviewJobUsesItsCapturedCollectionPath()
    {
        var job = new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://ado.example/tfs/collection", "project", "repository", 1, 1);

        Assert.Equal("https://ado.example", job.HostBaseUrl);
        Assert.Equal("AzureDevOps:https://ado.example/tfs/collection", ProviderSourceIdentity.FromReviewJob(job).Value);
    }
}
