// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text;
using System.Text.Json;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
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

    [Fact]
    public async Task FetchAsync_AppInstallation_UsesInstallationTokenToLoadExcludeFile()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var connectionRepository = GitHubAppTestHelpers.CreateAppInstallationConnectionRepository(clientId, host);

        string? excludeFileAuthorization = null;
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("GitHubProvider")
            .Returns(
                new HttpClient(
                    new StubHttpMessageHandler(request => Task.FromResult(
                        request.RequestUri!.AbsoluteUri switch
                        {
                            "https://api.github.com/app/installations/789012" => CreateJsonResponse(new { account = new { login = "acme-platform" } }),
                            "https://api.github.com/app/installations/789012/access_tokens" => CreateJsonResponse(
                                new
                                {
                                    token = "installation-token",
                                    expires_at = DateTimeOffset.UtcNow.AddHours(1),
                                }),
                            "https://api.github.com/repos/acme/propr/contents/.meister-propr%2Fexclude?ref=main" =>
                                CaptureExcludeFile(request),
                            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                        }))));

        var sut = new GitHubRepositoryExclusionFetcher(
            connectionRepository,
            httpClientFactory,
            Substitute.For<ILogger<GitHubRepositoryExclusionFetcher>>());

        var result = await sut.FetchAsync(
            "https://github.com",
            "acme",
            "acme/propr",
            "main",
            clientId,
            CancellationToken.None);

        Assert.Equal("installation-token", excludeFileAuthorization);
        Assert.Contains("openapi.json", result.Patterns);
        return;

        HttpResponseMessage CaptureExcludeFile(HttpRequestMessage request)
        {
            excludeFileAuthorization = request.Headers.Authorization?.Parameter;
            return CreateJsonResponse(
                new
                {
                    content = Convert.ToBase64String(Encoding.UTF8.GetBytes("openapi.json\n")),
                    encoding = "base64",
                });
        }
    }

    private static HttpResponseMessage CreateJsonResponse<T>(T payload)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload)),
        };
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

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return responder(request);
        }
    }
}
