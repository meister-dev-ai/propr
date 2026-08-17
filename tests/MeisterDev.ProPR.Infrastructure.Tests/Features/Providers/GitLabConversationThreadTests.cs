// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Security;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers;

/// <summary>
///     GitLab returns a comment standing on the merge request and a comment on a line of code from the same
///     discussions endpoint. Reviewing wants only the second; mention scanning wants both, asked for apart.
/// </summary>
public sealed class GitLabConversationThreadTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private const string Host = "https://gitlab.example.com";

    [Fact]
    public async Task ConversationThreads_AreTheCommentsStandingOnTheMergeRequest()
    {
        var sut = CreateFetcher();

        var threads = await sut.FetchConversationThreadsAsync(Host, "acme", "101", 42, ClientId);

        var thread = Assert.Single(threads);
        Assert.Equal("standalone-discussion", thread.ThreadId);
        Assert.Null(thread.FilePath);
        Assert.Null(thread.LineNumber);
        Assert.Equal("@propr what does this do?", Assert.Single(thread.Comments).Content);
    }

    /// <summary>
    ///     The same comment must not reach reviewing, which reads threads expecting them to be anchored to a
    ///     file, and must not be counted twice by anything reading both.
    /// </summary>
    [Fact]
    public async Task ReviewThreads_LeaveTheMergeRequestConversationOut()
    {
        var sut = CreateFetcher();

        var threads = await sut.FetchThreadsAsync(Host, "acme", "101", 42, ClientId);

        var thread = Assert.Single(threads);
        Assert.Equal("code-discussion", thread.ThreadId);
        Assert.Equal("src/Program.cs", thread.FilePath);
    }

    private static GitLabPullRequestFetcher CreateFetcher()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("GitLabProvider").Returns(new HttpClient(new StubHandler(Respond)));

        var host = new ProviderHostRef(ScmProvider.GitLab, Host);
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetOperationalConnectionAsync(ClientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(),
                    ClientId,
                    ScmProvider.GitLab,
                    Host,
                    ScmAuthenticationKind.PersonalAccessToken,
                    "GitLab",
                    "provider-token",
                    true));

        return new GitLabPullRequestFetcher(new GitLabConnectionVerifier(connections, factory), factory);
    }

    private static HttpResponseMessage Respond(HttpRequestMessage request)
    {
        var uri = request.RequestUri!.AbsoluteUri;

        if (uri.EndsWith("/api/v4/user", StringComparison.Ordinal))
        {
            return Json(new { username = "propr" });
        }

        if (uri.Contains("/discussions", StringComparison.Ordinal))
        {
            return Json(
                new object[]
                {
                    new
                    {
                        id = "standalone-discussion",
                        individual_note = true,
                        notes = new object[]
                        {
                            new
                            {
                                id = 9001L,
                                body = "@propr what does this do?",
                                created_at = DateTimeOffset.UtcNow,
                                system = false,
                                resolved = false,
                                author = new { username = "developer" },
                            },
                        },
                    },
                    new
                    {
                        id = "code-discussion",
                        individual_note = false,
                        notes = new object[]
                        {
                            new
                            {
                                id = 9002L,
                                body = "This sorts ascending.",
                                created_at = DateTimeOffset.UtcNow,
                                system = false,
                                resolved = false,
                                author = new { username = "developer" },
                                position = new { new_path = "src/Program.cs", new_line = 405 },
                            },
                        },
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
