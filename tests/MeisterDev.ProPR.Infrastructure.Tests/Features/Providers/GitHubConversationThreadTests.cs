// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers;

/// <summary>
///     GitHub serves the comments standing on the pull request from the issue-comment listing, which is the
///     second place mention scanning looks. A question asked there has to name its author the same way one
///     asked on a line of code does, or the same person counts twice.
/// </summary>
public sealed class GitHubConversationThreadTests
{
    private const string Host = "https://github.com";

    private static readonly Guid ClientId = Guid.NewGuid();

    [Fact]
    public async Task ConversationComments_CarryTheAccountsNumericId()
    {
        var sut = CreateFetcher(user: new { login = "developer", id = 4242 });

        var threads = await sut.FetchConversationThreadsAsync(Host, "acme", "acme/propr", 42, ClientId);

        var thread = Assert.Single(threads);
        Assert.Null(thread.FilePath);
        var comment = Assert.Single(thread.Comments);
        Assert.Equal("@propr what does this do?", comment.Content);
        Assert.Equal("4242", comment.AuthorNativeId);
    }

    [Fact]
    public async Task ConversationComments_WithoutAUser_CarryNoNativeId()
    {
        var sut = CreateFetcher(user: null);

        var threads = await sut.FetchConversationThreadsAsync(Host, "acme", "acme/propr", 42, ClientId);

        Assert.Null(Assert.Single(Assert.Single(threads).Comments).AuthorNativeId);
    }

    // GitHub issues no account id of zero, so a zero comes from the field being missing. Naming an account by
    // it would merge every such comment's author into one person.
    [Fact]
    public async Task ConversationComments_WithAnAccountIdOfZero_CarryNoNativeId()
    {
        var sut = CreateFetcher(user: new { login = "developer", id = 0 });

        var threads = await sut.FetchConversationThreadsAsync(Host, "acme", "acme/propr", 42, ClientId);

        Assert.Null(Assert.Single(Assert.Single(threads).Comments).AuthorNativeId);
    }

    private static GitHubPullRequestFetcher CreateFetcher(object? user)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("GitHubProvider").Returns(new HttpClient(new StubHandler(request => Respond(request, user))));

        var host = new ProviderHostRef(ScmProvider.GitHub, Host);
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetOperationalConnectionAsync(ClientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(),
                    ClientId,
                    ScmProvider.GitHub,
                    Host,
                    ScmAuthenticationKind.PersonalAccessToken,
                    "GitHub",
                    "provider-token",
                    true));

        return new GitHubPullRequestFetcher(new GitHubConnectionVerifier(connections, factory), factory);
    }

    private static HttpResponseMessage Respond(HttpRequestMessage request, object? user)
    {
        var uri = request.RequestUri!.AbsoluteUri;

        if (uri.EndsWith("/user", StringComparison.Ordinal))
        {
            return Json(new { login = "propr" });
        }

        if (uri.Contains("/issues/42/comments", StringComparison.Ordinal))
        {
            return Json(
                new object[]
                {
                    new
                    {
                        id = 9001L,
                        body = "@propr what does this do?",
                        created_at = DateTimeOffset.UtcNow,
                        user,
                    },
                });
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json<T>(T payload)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload)),
        };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }
}
