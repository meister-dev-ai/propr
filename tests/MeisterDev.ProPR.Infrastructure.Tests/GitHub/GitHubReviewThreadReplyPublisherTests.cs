// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.GitHub;

public sealed class GitHubReviewThreadReplyPublisherTests
{
    private const string ThreadNodeId = "PRRT_kwDOABCD1234";
    private const string UserUri = "https://api.github.com/user";
    private const string GraphQlUri = "https://api.github.com/graphql";

    [Fact]
    public void ProviderAdapters_RegisterGitHubThreadReplyPublisherUnderNeutralInterface()
    {
        var services = new ServiceCollection();

        services.AddSingleton(Substitute.For<IClientScmConnectionRepository>());
        services.AddGitHubProviderAdapters();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var replyPublisher = scope.ServiceProvider
            .GetServices<IReviewThreadReplyPublisher>()
            .Single(service => service.Provider == ScmProvider.GitHub);

        Assert.IsType<GitHubReviewThreadReplyPublisher>(replyPublisher);
    }

    [Fact]
    public async Task ReplyAsync_PostsTheThreadReplyMutationAndReturnsTheCreatedCommentDatabaseId()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var thread = CreateThread(host);
        var connectionRepository = CreateConnectionRepository(clientId, host);

        string? mutationUri = null;
        string? mutationBody = null;
        var httpClientFactory = CreateHttpClientFactory(async request =>
        {
            if (request.RequestUri!.AbsoluteUri == UserUri)
            {
                return CreateJsonResponse(new { login = "meister-dev" });
            }

            mutationUri = request.RequestUri.AbsoluteUri;
            mutationBody = await request.Content!.ReadAsStringAsync();
            return CreateJsonResponse(
                new
                {
                    data = new
                    {
                        addPullRequestReviewThreadReply = new
                        {
                            comment = new { id = "PRRC_kwDOABCD5678", databaseId = 9001L },
                        },
                    },
                });
        });

        var sut = new GitHubReviewThreadReplyPublisher(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var commentId = await sut.ReplyAsync(clientId, thread, "Fixed in the latest push.");

        Assert.Equal(GraphQlUri, mutationUri);
        Assert.NotNull(mutationBody);
        Assert.Contains("addPullRequestReviewThreadReply", mutationBody, StringComparison.Ordinal);
        Assert.Contains("pullRequestReviewThreadId", mutationBody, StringComparison.Ordinal);
        Assert.Contains(ThreadNodeId, mutationBody, StringComparison.Ordinal);
        Assert.Contains("Fixed in the latest push.", mutationBody, StringComparison.Ordinal);

        // The REST numeric id, not the node id: everything else that identifies a GitHub comment, including
        // thread ownership, keys on that encoding.
        Assert.Equal("9001", commentId);
    }

    [Fact]
    public async Task ReplyAsync_WhenGraphQlRefusesForPermissions_NamesTheMissingPermission()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var thread = CreateThread(host);
        var connectionRepository = CreateConnectionRepository(clientId, host);

        // GitHub answers a permission refusal with HTTP 200 and a FORBIDDEN entry in the errors array.
        var httpClientFactory = CreateHttpClientFactory(request => Task.FromResult(
            request.RequestUri!.AbsoluteUri == UserUri
                ? CreateJsonResponse(new { login = "meister-dev" })
                : CreateJsonResponse(
                    new
                    {
                        data = (object?)null,
                        errors = new[]
                        {
                            new { type = "FORBIDDEN", message = "Resource not accessible by integration" },
                        },
                    })));

        var sut = new GitHubReviewThreadReplyPublisher(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ReplyAsync(clientId, thread, "Fixed in the latest push."));

        Assert.Contains("Resource not accessible by integration", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Pull requests read and write", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplyAsync_WhenTokenIsRejected_SurfacesTheRejectionStatus()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var thread = CreateThread(host);
        var connectionRepository = CreateConnectionRepository(clientId, host);

        var httpClientFactory = CreateHttpClientFactory(request => Task.FromResult(
            request.RequestUri!.AbsoluteUri == UserUri
                ? CreateJsonResponse(new { login = "meister-dev" })
                : new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("Bad credentials"),
                }));

        var sut = new GitHubReviewThreadReplyPublisher(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ReplyAsync(clientId, thread, "Fixed in the latest push."));

        Assert.Contains("401", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Pull requests read and write", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Bad credentials", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplyAsync_WhenMutationReturnsNoComment_IsNotReportedAsPosted()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var thread = CreateThread(host);
        var connectionRepository = CreateConnectionRepository(clientId, host);

        // A refusal wearing a success shape: two hundred, no errors, and a payload with nothing in it.
        var httpClientFactory = CreateHttpClientFactory(request => Task.FromResult(
            request.RequestUri!.AbsoluteUri == UserUri
                ? CreateJsonResponse(new { login = "meister-dev" })
                : CreateJsonResponse(new { data = new { addPullRequestReviewThreadReply = new { comment = (object?)null } } })));

        var sut = new GitHubReviewThreadReplyPublisher(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ReplyAsync(clientId, thread, "Fixed in the latest push."));

        Assert.Contains("returned no comment", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplyAsync_NumericThreadId_IsRefusedAsNotAReviewThread()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        var thread = new ReviewThreadRef(review, "555", "src/file.ts", 18, isReviewerOwned: true);
        var connectionRepository = CreateConnectionRepository(clientId, host);

        var requests = 0;
        var httpClientFactory = CreateHttpClientFactory(_ =>
        {
            requests++;
            return Task.FromResult(CreateJsonResponse(new { login = "meister-dev" }));
        });

        var sut = new GitHubReviewThreadReplyPublisher(
            new GitHubConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ReplyAsync(clientId, thread, "Fixed in the latest push."));

        Assert.Contains("GraphQL node id", exception.Message, StringComparison.Ordinal);
        Assert.Contains("555", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, requests);
    }

    [Fact]
    public void FormatReplyText_NeutralizesMarkupWithoutManglingQuotedCode()
    {
        const string input = "Use \"--no-verify\" only after removing <script>alert('xss')</script>.";

        var reply = GitHubReviewThreadReplyPublisher.FormatReplyText(input);

        Assert.Contains("\"--no-verify\"", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("&quot;", reply, StringComparison.Ordinal);
        Assert.Equal(-1, reply.IndexOf("<script>", StringComparison.Ordinal));
    }

    private static ReviewThreadRef CreateThread(ProviderHostRef host)
    {
        var repository = new RepositoryRef(host, "101", "acme", "acme/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        return new ReviewThreadRef(review, ThreadNodeId, "src/file.ts", 18, isReviewerOwned: true);
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

    private static IHttpClientFactory CreateHttpClientFactory(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("GitHubProvider").Returns(new HttpClient(new StubHttpMessageHandler(responder)));
        return factory;
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
