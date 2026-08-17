// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using System.Text.Json;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Forgejo.Security;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitHub.Security;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.Features.Providers;

/// <summary>
///     Answering where the provider offers no thread to reply into: a new comment opening with a quote of the
///     question, which is what those providers' own quote reply produces.
/// </summary>
public sealed class QuotedReplyPublisherTests
{
    private static readonly Guid ClientId = Guid.NewGuid();

    [Fact]
    public async Task Forgejo_AnswersOnThePullRequestQuotingTheQuestion()
    {
        var requests = new List<(string Uri, string Body)>();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://forgejo.example");
        var factory = CreateFactory("ForgejoProvider", requests, "https://forgejo.example/api/v1/user", new { login = "propr" });

        var sut = new ForgejoReviewThreadReplyPublisher(
            new ForgejoConnectionVerifier(Connections(ScmProvider.Forgejo, host), factory),
            factory);

        var commentId = await sut.ReplyAsync(
            ClientId,
            Thread(host, filePath: "Program.cs", lineNumber: 405),
            "It sorts ascending and then takes three.",
            CancellationToken.None,
            "@propr why does this sort ascending?");

        // A review id is not a comment id, and provenance is keyed on comment ids, so nothing is reported
        // rather than something that could match an unrelated comment sharing the number.
        Assert.Null(commentId);

        var posted = Assert.Single(requests, request => request.Uri.Contains("/pulls/42/reviews", StringComparison.Ordinal));

        // The blockquote marker has to reach the provider as a marker. Escaped, the answer would open with a
        // literal "&gt;" and quote nothing.
        Assert.StartsWith("> @propr why does this sort ascending?", ReadPostedBody(posted.Body), StringComparison.Ordinal);
        Assert.EndsWith("It sorts ascending and then takes three.", ReadPostedBody(posted.Body), StringComparison.Ordinal);
    }

    /// <summary>
    ///     Forgejo scopes tokens by unit: an issue comment needs write access to the repository's issues,
    ///     which nothing else in ProPR asks for and which a repository with its issues unit disabled cannot
    ///     grant. Publishing findings already goes through the review route, so answering needs nothing more.
    /// </summary>
    [Fact]
    public async Task Forgejo_AnswersThroughTheRouteReviewingAlreadyUses()
    {
        var requests = new List<(string Uri, string Body)>();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://forgejo.example");
        var factory = CreateFactory("ForgejoProvider", requests, "https://forgejo.example/api/v1/user", new { login = "propr" });

        var sut = new ForgejoReviewThreadReplyPublisher(
            new ForgejoConnectionVerifier(Connections(ScmProvider.Forgejo, host), factory),
            factory);

        await sut.ReplyAsync(
            ClientId,
            Thread(host, filePath: null, lineNumber: null),
            "Answered.",
            CancellationToken.None,
            "Question?");

        Assert.DoesNotContain(requests, request => request.Uri.Contains("/issues/", StringComparison.Ordinal));

        // Submitted, not left pending, and a comment rather than a verdict on the change.
        using var document = JsonDocument.Parse(Assert.Single(requests, request => request.Uri.Contains("/pulls/42/reviews", StringComparison.Ordinal)).Body);
        Assert.Equal("COMMENT", document.RootElement.GetProperty("event").GetString());
    }

    /// <summary>
    ///     Guided selection stores a repository by the provider's own id, because that survives a rename. The
    ///     API is addressed by owner and name, so the pair is looked up rather than assembled from the scope
    ///     and the id, which would be shaped like a path and address nothing.
    /// </summary>
    [Theory]
    [InlineData("101", true)]
    [InlineData("acme/platform", false)]
    public async Task Forgejo_AddressesTheRepositoryByOwnerAndName(string repositoryId, bool expectsLookup)
    {
        var requests = new List<(string Uri, string Body)>();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://forgejo.example");
        var factory = CreateFactory("ForgejoProvider", requests, "https://forgejo.example/api/v1/user", new { login = "propr" });

        var sut = new ForgejoReviewThreadReplyPublisher(
            new ForgejoConnectionVerifier(Connections(ScmProvider.Forgejo, host), factory),
            factory);

        await sut.ReplyAsync(
            ClientId,
            Thread(host, filePath: null, lineNumber: null, repositoryId: repositoryId),
            "Answered.",
            CancellationToken.None,
            "Question?");

        Assert.Equal(
            expectsLookup,
            requests.Any(request => request.Uri.Contains("/repositories/101", StringComparison.Ordinal)));
        Assert.Contains(
            requests,
            request => request.Uri.Contains("/repos/acme/platform/pulls/42/reviews", StringComparison.Ordinal));
    }

    /// <summary>The same, for a GitHub question in the pull request conversation.</summary>
    [Fact]
    public async Task GitHub_AddressesTheRepositoryByOwnerAndName()
    {
        var requests = new List<(string Uri, string Body)>();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var factory = CreateFactory("GitHubProvider", requests, "https://api.github.com/user", new { login = "propr" });

        var sut = new GitHubReviewThreadReplyPublisher(
            new GitHubConnectionVerifier(Connections(ScmProvider.GitHub, host), factory),
            factory);

        await sut.ReplyAsync(
            ClientId,
            Thread(host, filePath: null, lineNumber: null, repositoryId: "101"),
            "Answered.",
            CancellationToken.None,
            "Question?");

        Assert.Contains(requests, request => request.Uri.Contains("/repositories/101", StringComparison.Ordinal));
        Assert.Contains(
            requests,
            request => request.Uri.Contains("/repos/acme/platform/issues/42/comments", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Forgejo has no thread to reply into even for a question on a line of code, so the answer lands on
    ///     the pull request either way and the quote is what says which comment it answers.
    /// </summary>
    [Fact]
    public async Task Forgejo_AnswersOnThePullRequestEvenForAQuestionOnALineOfCode()
    {
        var requests = new List<(string Uri, string Body)>();
        var host = new ProviderHostRef(ScmProvider.Forgejo, "https://forgejo.example");
        var factory = CreateFactory("ForgejoProvider", requests, "https://forgejo.example/api/v1/user", new { login = "propr" });

        var sut = new ForgejoReviewThreadReplyPublisher(
            new ForgejoConnectionVerifier(Connections(ScmProvider.Forgejo, host), factory),
            factory);

        await sut.ReplyAsync(
            ClientId,
            Thread(host, filePath: "Program.cs", lineNumber: 405),
            "Answered.",
            CancellationToken.None,
            "Question?");

        // One shape, whatever the question was attached to: no inline comment at the path and line, because
        // Forgejo has no conversation there to join.
        var posted = Assert.Single(requests, request => request.Uri.Contains("/pulls/42/reviews", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(posted.Body);
        Assert.False(document.RootElement.TryGetProperty("comments", out _));
    }

    /// <summary>
    ///     A GitHub question in the pull request conversation belongs to no review thread. The GraphQL reply
    ///     mutation has nothing to address, so the answer is a quoted comment on the issue timeline.
    /// </summary>
    [Fact]
    public async Task GitHub_AnswersAConversationQuestionWithAQuotedComment()
    {
        var requests = new List<(string Uri, string Body)>();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var factory = CreateFactory("GitHubProvider", requests, "https://api.github.com/user", new { login = "propr" });

        var sut = new GitHubReviewThreadReplyPublisher(
            new GitHubConnectionVerifier(Connections(ScmProvider.GitHub, host), factory),
            factory);

        var commentId = await sut.ReplyAsync(
            ClientId,
            Thread(host, filePath: null, lineNumber: null),
            "It sorts ascending and then takes three.",
            CancellationToken.None,
            "@propr why does this sort ascending?");

        Assert.Equal("9001", commentId);
        Assert.DoesNotContain(requests, request => request.Uri.Contains("graphql", StringComparison.Ordinal));
        var posted = Assert.Single(requests, request => request.Uri.Contains("/issues/42/comments", StringComparison.Ordinal));
        Assert.StartsWith("> @propr why does this sort ascending?", ReadPostedBody(posted.Body), StringComparison.Ordinal);
    }

    /// <summary>
    ///     A question on a line of code still goes into its review thread, where a quote would only repeat
    ///     what the reader is already looking at.
    /// </summary>
    [Fact]
    public async Task GitHub_AnswersACodeQuestionInItsThreadWithoutQuoting()
    {
        var requests = new List<(string Uri, string Body)>();
        var host = new ProviderHostRef(ScmProvider.GitHub, "https://github.com");
        var factory = CreateFactory("GitHubProvider", requests, "https://api.github.com/user", new { login = "propr" });

        var sut = new GitHubReviewThreadReplyPublisher(
            new GitHubConnectionVerifier(Connections(ScmProvider.GitHub, host), factory),
            factory);

        await sut.ReplyAsync(
            ClientId,
            Thread(host, filePath: "Program.cs", lineNumber: 405, threadId: "PRRT_kwDOABCD"),
            "Answered.",
            CancellationToken.None,
            "Question?");

        var posted = Assert.Single(requests, request => request.Uri.Contains("graphql", StringComparison.Ordinal));
        Assert.Equal("Answered.", ReadPostedBody(posted.Body));
        Assert.DoesNotContain(requests, request => request.Uri.Contains("/issues/", StringComparison.Ordinal));
    }

    /// <summary>Reads the comment text out of a recorded request, REST body or GraphQL variable alike.</summary>
    private static string ReadPostedBody(string requestJson)
    {
        using var document = JsonDocument.Parse(requestJson);
        var root = document.RootElement;

        if (root.TryGetProperty("variables", out var variables))
        {
            return variables.GetProperty("body").GetString() ?? string.Empty;
        }

        return root.GetProperty("body").GetString() ?? string.Empty;
    }

    private static ReviewThreadRef Thread(
        ProviderHostRef host,
        string? filePath,
        int? lineNumber,
        string threadId = "5001",
        string repositoryId = "acme/platform")
    {
        // The project path is deliberately something a caller could have assembled wrongly, so a publisher
        // that trusts it instead of resolving the identifier is caught here.
        var repository = new RepositoryRef(host, repositoryId, "acme", "acme/3");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "42", 42);
        return new ReviewThreadRef(review, threadId, filePath, lineNumber, false);
    }

    private static IHttpClientFactory CreateFactory(
        string clientName,
        List<(string Uri, string Body)> requests,
        string identityUri,
        object identityPayload)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(clientName).Returns(new HttpClient(new RecordingHandler(requests, identityUri, identityPayload)));
        return factory;
    }

    private static IClientScmConnectionRepository Connections(ScmProvider provider, ProviderHostRef host)
    {
        var connections = Substitute.For<IClientScmConnectionRepository>();
        connections.GetOperationalConnectionAsync(ClientId, host, Arg.Any<CancellationToken>())
            .Returns(
                new ClientScmConnectionCredentialDto(
                    Guid.NewGuid(),
                    ClientId,
                    provider,
                    host.HostBaseUrl,
                    ScmAuthenticationKind.PersonalAccessToken,
                    provider.ToString(),
                    "provider-token",
                    true));
        return connections;
    }

    private sealed class RecordingHandler(
        List<(string Uri, string Body)> requests,
        string identityUri,
        object identityPayload) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.AbsoluteUri;
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            if (string.Equals(uri, identityUri, StringComparison.Ordinal))
            {
                return Json(identityPayload);
            }

            requests.Add((uri, body));

            if (uri.Contains("graphql", StringComparison.Ordinal))
            {
                return Json(
                    new
                    {
                        data = new
                        {
                            addPullRequestReviewThreadReply = new { comment = new { id = "IC_1", databaseId = 9001L } },
                        },
                    });
            }

            // A repository looked up by its provider-native id answers with the owner/name pair.
            if (uri.Contains("/repositories/", StringComparison.Ordinal))
            {
                return Json(new { full_name = "acme/platform" });
            }

            return Json(new { id = 9001L });
        }

        private static HttpResponseMessage Json<T>(T payload)
        {
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload)),
            };
        }
    }
}
