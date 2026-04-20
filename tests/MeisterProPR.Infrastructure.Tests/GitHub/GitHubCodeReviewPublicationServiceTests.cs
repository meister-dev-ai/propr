// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterProPR.Application.DTOs;
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
        httpClientFactory.CreateClient("GitHubProvider")
            .Returns(
                new HttpClient(
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

                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                    })));

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

    private static HttpResponseMessage CreateJsonResponse<T>(T payload)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload)),
        };
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
