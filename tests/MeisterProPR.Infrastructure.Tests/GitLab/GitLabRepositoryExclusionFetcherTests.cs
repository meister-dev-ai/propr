// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Interfaces;
using MeisterProPR.Infrastructure.Features.Providers.GitLab.Reviewing;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.GitLab;

public sealed class GitLabRepositoryExclusionFetcherTests
{
    [Fact]
    public async Task FetchAsync_WithPatterns_ReturnsParsedExclusionRules()
    {
        var sut = new TestableGitLabRepositoryExclusionFetcher();
        sut.SetContent("openapi.json\nsrc/generated/**\n");

        var result = await sut.FetchAsync(
            "https://gitlab.example.com",
            "acme",
            "repo",
            "main",
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.Equal(2, result.Patterns.Count);
        Assert.Contains("openapi.json", result.Patterns);
        Assert.Contains("src/generated/**", result.Patterns);
        Assert.False(result.IsDefault);
    }

    [Fact]
    public async Task FetchAsync_AbsentFile_ReturnsDefault()
    {
        var sut = new TestableGitLabRepositoryExclusionFetcher();
        sut.SetFileAbsent();

        var result = await sut.FetchAsync(
            "https://gitlab.example.com",
            "acme",
            "repo",
            "main",
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.True(result.IsDefault);
    }

    [Fact]
    public async Task FetchAsync_EmptyFile_ReturnsEmpty()
    {
        var sut = new TestableGitLabRepositoryExclusionFetcher();
        sut.SetContent(string.Empty);

        var result = await sut.FetchAsync(
            "https://gitlab.example.com",
            "acme",
            "repo",
            "main",
            Guid.NewGuid(),
            CancellationToken.None);

        Assert.False(result.IsDefault);
        Assert.False(result.HasPatterns);
    }

    private sealed class TestableGitLabRepositoryExclusionFetcher : GitLabRepositoryExclusionFetcher
    {
        private string? _content;
        private bool _fileExists = true;

        public TestableGitLabRepositoryExclusionFetcher()
            : base(
                Substitute.For<IClientScmConnectionRepository>(),
                Substitute.For<IHttpClientFactory>(),
                Substitute.For<ILogger<GitLabRepositoryExclusionFetcher>>())
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
