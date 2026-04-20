// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Reviewing;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.GitHub;

public sealed class GitHubRepositoryExclusionFetcherTests
{
    [Fact]
    public async Task FetchAsync_WithPatterns_ReturnsParsedExclusionRules()
    {
        var sut = new TestableGitHubRepositoryExclusionFetcher();
        sut.SetContent("openapi.json\nsrc/generated/**\n");

        var result = await sut.FetchAsync(
            "https://github.com",
            "acme",
            "123456",
            "main",
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Equal(2, result.Patterns.Count);
        Assert.Contains("openapi.json", result.Patterns);
        Assert.Contains("src/generated/**", result.Patterns);
        Assert.True(result.Matches("openapi.json"));
        Assert.False(result.IsDefault);
    }

    [Fact]
    public async Task FetchAsync_AbsentFile_ReturnsDefault()
    {
        var sut = new TestableGitHubRepositoryExclusionFetcher();
        sut.SetFileAbsent();

        var result = await sut.FetchAsync(
            "https://github.com",
            "acme",
            "123456",
            "main",
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.IsDefault);
    }

    [Fact]
    public async Task FetchAsync_EmptyFile_ReturnsEmpty()
    {
        var sut = new TestableGitHubRepositoryExclusionFetcher();
        sut.SetContent(string.Empty);

        var result = await sut.FetchAsync(
            "https://github.com",
            "acme",
            "123456",
            "main",
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.IsDefault);
        Assert.False(result.HasPatterns);
    }

    private sealed class TestableGitHubRepositoryExclusionFetcher : GitHubRepositoryExclusionFetcher
    {
        private string? _content;
        private bool _fileExists = true;

        public TestableGitHubRepositoryExclusionFetcher()
            : base(
                Substitute.For<IClientScmConnectionRepository>(),
                Substitute.For<IHttpClientFactory>(),
                Substitute.For<ILogger<GitHubRepositoryExclusionFetcher>>())
        {
        }

        public void SetContent(string content)
        {
            this._content = content;
            this._fileExists = true;
        }

        public void SetFileAbsent()
        {
            this._fileExists = false;
        }

        protected override Task<string?> FetchExcludeFileAsync(
            string organizationUrl,
            string repositoryId,
            string targetBranch,
            Guid? clientId,
            CancellationToken cancellationToken)
        {
            if (!this._fileExists)
            {
                return Task.FromResult<string?>(null);
            }

            return Task.FromResult<string?>(this._content);
        }
    }
}
