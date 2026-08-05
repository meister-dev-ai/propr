// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.GitHub;

public sealed class GitHubCodeReviewPublicationServiceTests
{
    private const string ThreadNodeId = "PRRT_kwDOABCD1234";
    private const string UserUri = "https://api.github.com/user";
    private const string GraphQlUri = "https://api.github.com/graphql";
    private const string ReviewsUri = "https://api.github.com/repos/acme/propr/pulls/42/reviews";
    private const string ReviewCommentsUri = "https://api.github.com/repos/acme/propr/pulls/42/reviews/555/comments";

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
            [new PrCommentThread("1", "src/file.ts", 18, [new PrThreadComment("Bot", "Existing thread")])]);

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
            new StubHttpMessageHandler(request => Task.FromResult(
                request.RequestUri!.AbsoluteUri switch
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
            new StubHttpMessageHandler(request => Task.FromResult(
                request.RequestUri!.AbsoluteUri switch
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

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.PublishReviewAsync(clientId, review, revision, result, reviewer));

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
                    "https://api.github.com/repos/acme/propr/pulls/42/reviews" => CreateJsonResponse(
                        new { message = "Resource not accessible by integration" },
                        HttpStatusCode.Forbidden),
                    _ => CreateJsonResponse(new { message = "Not Found" }, HttpStatusCode.NotFound),
                })));
        httpClientFactory.CreateClient("GitHubProvider").Returns(httpClient);

        var sut = new GitHubCodeReviewPublicationService(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.PublishReviewAsync(clientId, review, revision, result, reviewer));

        Assert.Contains("no longer has permission", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Resource not accessible by integration", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublishReviewAsync_RecordsAThreadIdTheThreadStatusWriterAccepts()
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

        var connectionRepository = CreateConnectionRepository(clientId, host);

        string? mutationBody = null;
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler(async request =>
            {
                if (request.RequestUri!.AbsoluteUri == GraphQlUri)
                {
                    var graphQlBody = await request.Content!.ReadAsStringAsync();
                    if (graphQlBody.Contains("resolveReviewThread", StringComparison.Ordinal))
                    {
                        mutationBody = graphQlBody;
                        return CreateJsonResponse(new { data = new { resolveReviewThread = new { thread = new { id = ThreadNodeId, isResolved = true } } } });
                    }

                    return CreateReviewThreadsResponse(ThreadNodeId, 9001L);
                }

                return request.RequestUri.AbsoluteUri switch
                {
                    UserUri => CreateJsonResponse(new { login = "meister-dev" }),
                    ReviewsUri => CreateJsonResponse(new { id = 555L }),
                    ReviewCommentsUri => CreateJsonResponse(new[] { new { id = 9001L, path = "src/file.ts", line = 18 } }),
                    _ => CreateJsonResponse(new { message = "Not Found" }, HttpStatusCode.NotFound),
                };
            }));
        httpClientFactory.CreateClient("GitHubProvider").Returns(httpClient);

        var publicationService = new GitHubCodeReviewPublicationService(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var diagnostics = await publicationService.PublishReviewAsync(clientId, review, revision, result, reviewer);

        var reference = Assert.Single(diagnostics.PostedComments);
        Assert.NotNull(reference.ProviderThreadId);

        // The identifier publishing recorded is handed straight to the writer that resolves the thread, which
        // is the journey that made a review id recorded here reach a call expecting a thread node id.
        var statusWriter = new GitHubReviewThreadStatusWriter(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);
        var thread = new ReviewThreadRef(
            review,
            reference.ProviderThreadId!,
            reference.FilePath,
            reference.Line,
            isReviewerOwned: true);

        await statusWriter.UpdateThreadStatusAsync(clientId, thread, "fixed");

        Assert.NotNull(mutationBody);
        Assert.Contains(ThreadNodeId, mutationBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishReviewAsync_WhenTheThreadLookupIsRefused_RecordsCommentIdsWithoutAThreadId()
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

        var connectionRepository = CreateConnectionRepository(clientId, host);

        // GitHub refuses a query with two hundred and an errors array, and answers a partial refusal with a
        // data section alongside it. Ids read out of a traversal that reported an error are not trustworthy
        // enough to key a later write on, so the whole lookup is discarded rather than half believed.
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        using var httpClient = new HttpClient(
            new StubHttpMessageHandler(request => Task.FromResult(
                request.RequestUri!.AbsoluteUri switch
                {
                    UserUri => CreateJsonResponse(new { login = "meister-dev" }),
                    ReviewsUri => CreateJsonResponse(new { id = 555L }),
                    ReviewCommentsUri => CreateJsonResponse(new[] { new { id = 9001L, path = "src/file.ts", line = 18 } }),
                    GraphQlUri => CreateRefusedReviewThreadsResponse(ThreadNodeId, 9001L),
                    _ => CreateJsonResponse(new { message = "Not Found" }, HttpStatusCode.NotFound),
                })));
        httpClientFactory.CreateClient("GitHubProvider").Returns(httpClient);

        var sut = new GitHubCodeReviewPublicationService(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var diagnostics = await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        var reference = Assert.Single(diagnostics.PostedComments);
        Assert.Equal("9001", reference.ProviderCommentId);
        Assert.Null(reference.ProviderThreadId);
        Assert.Equal(1, diagnostics.PostedCount);
    }

    [Fact]
    public async Task PublishReviewAsync_CapturesCreatedCommentIdsFromReviewCommentsEndpoint()
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
            new StubHttpMessageHandler(request => Task.FromResult(
                request.RequestUri!.AbsoluteUri switch
                {
                    UserUri => CreateJsonResponse(new { login = "meister-dev" }),
                    ReviewsUri => CreateJsonResponse(new { id = 555L }),
                    ReviewCommentsUri => CreateJsonResponse(
                        new[]
                        {
                            new { id = 9001L, path = "src/file.ts", line = 18 },
                        }),
                    GraphQlUri => CreateReviewThreadsResponse(ThreadNodeId, 9001L),
                    _ => CreateJsonResponse(new { message = "Not Found" }, HttpStatusCode.NotFound),
                })));
        httpClientFactory.CreateClient("GitHubProvider").Returns(httpClient);

        var sut = new GitHubCodeReviewPublicationService(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var diagnostics = await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        var reference = Assert.Single(diagnostics.PostedComments);
        Assert.Equal("9001", reference.ProviderCommentId);

        // The node id of the thread the comment opened, which is what addresses a GitHub review thread. The
        // review id the same response also carries names a different object and can never address one.
        Assert.Equal(ThreadNodeId, reference.ProviderThreadId);
        Assert.Equal("src/file.ts", reference.FilePath);
        Assert.Equal(18, reference.Line);
        Assert.Equal(1, diagnostics.PostedCount);
    }

    [Fact]
    public async Task PublishReviewAsync_WhenCommentsEndpointUnavailable_PublishesWithEmptyPostedComments()
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
            new StubHttpMessageHandler(request => Task.FromResult(
                request.RequestUri!.AbsoluteUri switch
                {
                    "https://api.github.com/user" => CreateJsonResponse(new { login = "meister-dev" }),
                    "https://api.github.com/repos/acme/propr/pulls/42/reviews" => CreateJsonResponse(new { id = 555L }),
                    "https://api.github.com/repos/acme/propr/pulls/42/reviews/555/comments" => CreateJsonResponse(
                        new { message = "Not Found" },
                        HttpStatusCode.NotFound),
                    _ => CreateJsonResponse(new { message = "Not Found" }, HttpStatusCode.NotFound),
                })));
        httpClientFactory.CreateClient("GitHubProvider").Returns(httpClient);

        var sut = new GitHubCodeReviewPublicationService(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var diagnostics = await sut.PublishReviewAsync(clientId, review, revision, result, reviewer);

        Assert.Empty(diagnostics.PostedComments);
        Assert.Equal(1, diagnostics.PostedCount);
    }

    private static HttpResponseMessage CreateJsonResponse<T>(T payload, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload)),
        };
    }

    // The pull request's review threads as GraphQL reports them: each thread's node id alongside the database
    // ids of its comments, which is the only join between a created review comment and its thread.
    private static HttpResponseMessage CreateReviewThreadsResponse(string threadNodeId, long commentDatabaseId)
    {
        return CreateJsonResponse(new { data = BuildReviewThreadsData(threadNodeId, commentDatabaseId) });
    }

    private static HttpResponseMessage CreateRefusedReviewThreadsResponse(string threadNodeId, long commentDatabaseId)
    {
        return CreateJsonResponse(
            new
            {
                data = BuildReviewThreadsData(threadNodeId, commentDatabaseId),
                errors = new[] { new { type = "FORBIDDEN", message = "Resource not accessible by integration" } },
            });
    }

    private static object BuildReviewThreadsData(string threadNodeId, long commentDatabaseId)
    {
        return new
        {
            repository = new
            {
                pullRequest = new
                {
                    reviewThreads = new
                    {
                        nodes = new[]
                        {
                            new
                            {
                                id = threadNodeId,
                                comments = new
                                {
                                    nodes = new[] { new { databaseId = commentDatabaseId } },
                                },
                            },
                        },
                    },
                },
            },
        };
    }

    private static IClientScmConnectionRepository CreateConnectionRepository(Guid clientId, ProviderHostRef host)
    {
        var repository = Substitute.For<IClientScmConnectionRepository>();
        repository.GetOperationalConnectionAsync(clientId, host, Arg.Any<CancellationToken>())
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
        return repository;
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
