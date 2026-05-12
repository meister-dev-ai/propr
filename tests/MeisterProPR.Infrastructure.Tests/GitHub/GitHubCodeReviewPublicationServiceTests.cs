// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterProPR.Application.DTOs;
using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Reviewing;
using MeisterProPR.Infrastructure.Features.Providers.GitHub.Security;
using NSubstitute;

namespace MeisterProPR.Infrastructure.Tests.GitHub;

public sealed class GitHubCodeReviewPublicationServiceTests
{
    [Fact]
    public async Task PublishReviewAsync_PostsSummaryAndInlineCommentsToGitHubReviewApi()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot[bot]", "Meister Review Bot", true);
        var result = new ReviewResult(
            "Looks solid overall.",
            [
                new ReviewComment("src/file.ts", 18, CommentSeverity.Warning, "Guard this null case."),
                new ReviewComment(null, null, CommentSeverity.Info, "No blocking issues found."),
            ]);

        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(),
                    clientId,
                    ScmProvider.GitHub,
                    host.HostBaseUrl,
                    ScmAuthenticationKind.PersonalAccessToken,
                    "GitHub",
                    "ghp_test",
                    true));

        string? postedBody = null;
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler(async request =>
            {
                if (request.RequestUri!.AbsoluteUri == "https://api.github.com/user")
                {
                    return CreateJsonResponse(new { login = "meister-dev" });
                }

                if (request.RequestUri.AbsoluteUri ==
                    "https://api.github.com/repos/acme/propr/pulls/42/reviews")
                {
                    postedBody = await request.Content!.ReadAsStringAsync();
                    return CreateJsonResponse(new { id = 1 });
                }

                return CreateJsonResponse(new { message = "Not Found" }, HttpStatusCode.NotFound);
            }));
        httpClientFactory.CreateClient("GitHubProvider")
            .Returns(httpClient);

        var sut = new GitHubCodeReviewPublicationService(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        Assert.NotNull(postedBody);
        Assert.Contains("Looks solid overall.", postedBody, StringComparison.Ordinal);
        Assert.Contains("Guard this null case.", postedBody, StringComparison.Ordinal);
        Assert.Contains("No blocking issues found.", postedBody, StringComparison.Ordinal);
        Assert.Contains("src/file.ts", postedBody, StringComparison.Ordinal);
        Assert.Contains("head-sha", postedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishReviewAsync_NormalizesInlineCommentPathBeforePosting()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot[bot]", "Meister Review Bot", true);
        var result = new ReviewResult(
            "Looks solid overall.",
            [new ReviewComment("  /src/file.ts  ", 18, CommentSeverity.Warning, "Guard this null case.")]);

        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(),
                    clientId,
                    ScmProvider.GitHub,
                    host.HostBaseUrl,
                    ScmAuthenticationKind.PersonalAccessToken,
                    "GitHub",
                    "ghp_test",
                    true));

        string? postedBody = null;
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler(async request =>
            {
                if (request.RequestUri!.AbsoluteUri == "https://api.github.com/user")
                {
                    return CreateJsonResponse(new { login = "meister-dev" });
                }

                if (request.RequestUri.AbsoluteUri ==
                    "https://api.github.com/repos/acme/propr/pulls/42/reviews")
                {
                    postedBody = await request.Content!.ReadAsStringAsync();
                    return CreateJsonResponse(new { id = 1 });
                }

                return CreateJsonResponse(new { message = "Not Found" }, HttpStatusCode.NotFound);
            }));
        httpClientFactory.CreateClient("GitHubProvider")
            .Returns(httpClient);

        var sut = new GitHubCodeReviewPublicationService(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        Assert.NotNull(postedBody);
        using var document = JsonDocument.Parse(postedBody);
        var comments = document.RootElement.GetProperty("comments");
        Assert.Equal("src/file.ts", comments[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task PublishReviewAsync_WhenReviewHasNoInlineComments_SendsEmptyCommentsArray()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot[bot]", "Meister Review Bot", true);
        var result = new ReviewResult(
            "Looks solid overall.",
            [new ReviewComment(null, null, CommentSeverity.Info, "No blocking issues found.")]);

        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(),
                    clientId,
                    ScmProvider.GitHub,
                    host.HostBaseUrl,
                    ScmAuthenticationKind.PersonalAccessToken,
                    "GitHub",
                    "ghp_test",
                    true));

        string? postedBody = null;
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler(async request =>
            {
                if (request.RequestUri!.AbsoluteUri == "https://api.github.com/user")
                {
                    return CreateJsonResponse(new { login = "meister-dev" });
                }

                if (request.RequestUri.AbsoluteUri ==
                    "https://api.github.com/repos/acme/propr/pulls/42/reviews")
                {
                    postedBody = await request.Content!.ReadAsStringAsync();
                    return CreateJsonResponse(new { id = 1 });
                }

                return CreateJsonResponse(new { message = "Not Found" }, HttpStatusCode.NotFound);
            }));
        httpClientFactory.CreateClient("GitHubProvider")
            .Returns(httpClient);

        var sut = new GitHubCodeReviewPublicationService(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        Assert.NotNull(postedBody);
        Assert.Contains("\"comments\":[]", postedBody, StringComparison.Ordinal);
        Assert.Contains("No blocking issues found.", postedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishReviewAsync_IgnoresAdditivePublicationContextForGitHub()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot[bot]", "Meister Review Bot", true);
        var result = new ReviewResult("Looks solid overall.", []);
        var publicationContext = new ReviewPublicationContext(
            review,
            revision,
            reviewer,
            [new PrCommentThread(1, "src/file.ts", 18, [new PrThreadComment("Bot", "Existing thread")])]);

        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(),
                    clientId,
                    ScmProvider.GitHub,
                    host.HostBaseUrl,
                    ScmAuthenticationKind.PersonalAccessToken,
                    "GitHub",
                    "ghp_test",
                    true));

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler(request => Task.FromResult(request.RequestUri!.AbsoluteUri switch
            {
                "https://api.github.com/user" => CreateJsonResponse(new { login = "meister-dev" }),
                "https://api.github.com/repos/acme/propr/pulls/42/reviews" => CreateJsonResponse(new { id = 1 }),
                _ => CreateJsonResponse(new { message = "Not Found" }, HttpStatusCode.NotFound),
            })));
        httpClientFactory.CreateClient("GitHubProvider").Returns(httpClient);

        var sut = new GitHubCodeReviewPublicationService(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var diagnostics = await sut.PublishReviewAsync(clientId, review, revision, result, reviewer, publicationContext: publicationContext);

        Assert.Equal(0, diagnostics.PostedCount);
        Assert.Equal(0, diagnostics.SuppressedCount);
    }

    [Fact]
    public async Task PublishReviewAsync_WhenGitHubReturnsValidationError_IncludesResponseBodyInException()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot[bot]", "Meister Review Bot", true);
        var result = new ReviewResult(
            "Looks solid overall.",
            [new ReviewComment("src/file.ts", 18, CommentSeverity.Warning, "Guard this null case.")]);

        var connectionRepository = Substitute.For<IClientScmConnectionRepository>();
        connectionRepository.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(),
                    clientId,
                    ScmProvider.GitHub,
                    host.HostBaseUrl,
                    ScmAuthenticationKind.PersonalAccessToken,
                    "GitHub",
                    "ghp_test",
                    true));

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler(request => Task.FromResult(request.RequestUri!.AbsoluteUri switch
            {
                "https://api.github.com/user" => CreateJsonResponse(new { login = "meister-dev" }),
                "https://api.github.com/repos/acme/propr/pulls/42/reviews" => CreateJsonResponse(
                    new { message = "Review comments is invalid and Review threads is invalid" },
                    (HttpStatusCode)422),
                _ => CreateJsonResponse(new { message = "Not Found" }, HttpStatusCode.NotFound),
            })));
        httpClientFactory.CreateClient("GitHubProvider")
            .Returns(httpClient);

        var sut = new GitHubCodeReviewPublicationService(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.PublishReviewAsync(clientId, review, revision, result, reviewer));

        Assert.Contains("422", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Review comments is invalid", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishReviewAsync_AppInstallation_UsesInstallationAccessToken()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot[bot]", "Meister Review Bot", true);
        var result = new ReviewResult("Looks solid overall.", []);

        var connectionRepository = GitHubAppTestHelpers.CreateAppInstallationConnectionRepository(clientId, host);
        string? reviewAuthorization = null;
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler(request => Task.FromResult(request.RequestUri!.AbsoluteUri switch
            {
                "https://api.github.com/app/installations/789012" => CreateJsonResponse(
                    new { account = new { login = "acme-platform" } }),
                "https://api.github.com/app/installations/789012/access_tokens" => CreateJsonResponse(
                    new
                    {
                        token = "installation-token",
                        expires_at = DateTimeOffset.UtcNow.AddHours(1),
                    }),
                "https://api.github.com/repos/acme/propr/pulls/42/reviews" => CaptureAndReturnReviewResponse(request),
                _ => CreateJsonResponse(new { message = "Not Found" }, HttpStatusCode.NotFound),
            })));
        httpClientFactory.CreateClient("GitHubProvider").Returns(httpClient);

        var sut = new GitHubCodeReviewPublicationService(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        Assert.Equal("installation-token", reviewAuthorization);
        return;

        HttpResponseMessage CaptureAndReturnReviewResponse(HttpRequestMessage request)
        {
            reviewAuthorization = request.Headers.Authorization?.Parameter;
            return CreateJsonResponse(new { id = 1 });
        }
    }

    [Fact]
    public async Task PublishReviewAsync_AppInstallationPermissionLoss_ThrowsActionableInvalidOperationException()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var revision = new ReviewRevision("head-sha", "base-sha", null, "head-sha", "base-sha...head-sha");
        var reviewer = new ReviewerIdentity(host, "99", "meister-review-bot[bot]", "Meister Review Bot", true);
        var result = new ReviewResult("Looks solid overall.", []);

        var connectionRepository = GitHubAppTestHelpers.CreateAppInstallationConnectionRepository(clientId, host);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler(request => Task.FromResult(request.RequestUri!.AbsoluteUri switch
            {
                "https://api.github.com/app/installations/789012" => CreateJsonResponse(
                    new { account = new { login = "acme-platform" } }),
                "https://api.github.com/app/installations/789012/access_tokens" => CreateJsonResponse(
                    new
                    {
                        token = "installation-token",
                        expires_at = DateTimeOffset.UtcNow.AddHours(1),
                    }),
                "https://api.github.com/repos/acme/propr/pulls/42/reviews" => CreateJsonResponse(
                    new { message = "Resource not accessible by integration" },
                    HttpStatusCode.Forbidden),
                _ => CreateJsonResponse(new { message = "Not Found" }, HttpStatusCode.NotFound),
            })));
        httpClientFactory.CreateClient("GitHubProvider").Returns(httpClient);

        var sut = new GitHubCodeReviewPublicationService(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.PublishReviewAsync(clientId, review, revision, result, reviewer));

        Assert.Contains("no longer has permission", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Resource not accessible by integration", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage CreateJsonResponse<T>(T payload, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload)),
        };
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await responder(request);
            return new DelegatingDisposableHttpResponseMessage(response);
        }

        private sealed class DelegatingDisposableHttpResponseMessage : HttpResponseMessage
        {
            private readonly HttpResponseMessage _inner;

            public DelegatingDisposableHttpResponseMessage(HttpResponseMessage inner)
                : base(inner.StatusCode)
            {
                this._inner = inner;
                this.ReasonPhrase = inner.ReasonPhrase;
                this.Version = inner.Version;
                this.RequestMessage = inner.RequestMessage;
                this.Content = inner.Content;

                foreach (var header in inner.Headers)
                {
                    this.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    if (ReferenceEquals(this.Content, this._inner.Content))
                    {
                        this.Content = null;
                    }

                    base.Dispose(true);
                    this._inner.Dispose();
                    return;
                }

                base.Dispose(false);
            }
        }
    }
}
