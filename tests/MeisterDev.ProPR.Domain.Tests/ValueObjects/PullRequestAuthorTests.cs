// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Domain.Tests.ValueObjects;

public sealed class PullRequestAuthorTests
{
    [Fact]
    public void SameHostAndId_AreEqualDespiteDifferentNames()
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var renamed = new PullRequestAuthor(host, "4242", "octo-dev-renamed", "Octo Dev Renamed", false);
        var original = new PullRequestAuthor(host, "4242", "octo-dev", "Octo Dev", true);

        Assert.Equal(original, renamed);
        Assert.Equal(original.GetHashCode(), renamed.GetHashCode());
    }

    [Fact]
    public void SameIdOnTwoHosts_AreNotEqual()
    {
        var cloud = new PullRequestAuthor(
            new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.com"),
            "77",
            "octo-dev");
        var selfHosted = new PullRequestAuthor(
            new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com"),
            "77",
            "someone-else");

        Assert.NotEqual(cloud, selfHosted);
    }

    [Fact]
    public void SameIdOnTwoProviders_AreNotEqual()
    {
        var forgejo = new PullRequestAuthor(
            new ProviderHostRef(ScmProvider.Forgejo, "https://codeberg.example.com"),
            "88");
        var gitLab = new PullRequestAuthor(
            new ProviderHostRef(ScmProvider.GitLab, "https://codeberg.example.com"),
            "88");

        Assert.NotEqual(forgejo, gitLab);
    }

    [Fact]
    public void DifferentIdsOnOneHost_AreNotEqual()
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");

        Assert.NotEqual(new PullRequestAuthor(host, "4242"), new PullRequestAuthor(host, "4243"));
    }

    [Fact]
    public void BlankNames_AreStoredAsAbsent()
    {
        var host = new ProviderHostRef(ScmProvider.AzureDevOps, "https://dev.azure.com/acme");

        var author = new PullRequestAuthor(host, " 6f0c1a2b ", "  ", "  ");

        Assert.Equal("6f0c1a2b", author.ExternalUserId);
        Assert.Null(author.Login);
        Assert.Null(author.DisplayName);
        Assert.Null(author.IsBot);
    }

    [Fact]
    public void MissingExternalUserId_IsRejected()
    {
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");

        Assert.Throws<ArgumentException>(() => new PullRequestAuthor(host, " "));
    }
}
