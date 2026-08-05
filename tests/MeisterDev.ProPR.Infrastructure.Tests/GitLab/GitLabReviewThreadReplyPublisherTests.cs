// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.DependencyInjection;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Reviewing;
using MeisterDev.ProPR.Infrastructure.Features.Providers.GitLab.Security;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace MeisterDev.ProPR.Infrastructure.Tests.GitLab;

public sealed class GitLabReviewThreadReplyPublisherTests
{
    private const string DiscussionId = "6a9c1750b37d513a43987b574953fceb50b03ce7";
    private const string UserUri = "https://gitlab.example.com/api/v4/user";

    private const string NotesUri =
        "https://gitlab.example.com/api/v4/projects/101/merge_requests/42/discussions/" + DiscussionId + "/notes";

    [Fact]
    public void ProviderAdapters_RegisterGitLabThreadReplyPublisherUnderNeutralInterface()
    {
        var services = new ServiceCollection();

        services.AddSingleton(Substitute.For<IClientScmConnectionRepository>());
        services.AddGitLabProviderAdapters();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var replyPublisher = scope.ServiceProvider
            .GetServices<IReviewThreadReplyPublisher>()
            .Single(service => service.Provider == ScmProvider.GitLab);

        Assert.IsType<GitLabReviewThreadReplyPublisher>(replyPublisher);
    }

    [Fact]
    public async Task ReplyAsync_PostsANoteIntoTheDiscussionAndReturnsTheCreatedNoteId()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var thread = CreateThread(host);
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);

        HttpMethod? replyMethod = null;
        string? replyUri = null;
        string? replyBody = null;
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(async request =>
        {
            if (request.RequestUri!.AbsoluteUri == UserUri)
            {
                return GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" });
            }

            replyMethod = request.Method;
            replyUri = request.RequestUri.AbsoluteUri;
            replyBody = await request.Content!.ReadAsStringAsync();
            return GitLabTestHelpers.CreateJsonResponse(new { id = 1126L }, HttpStatusCode.Created);
        });

        var sut = new GitLabReviewThreadReplyPublisher(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var noteId = await sut.ReplyAsync(clientId, thread, "Fixed in the latest push.");

        Assert.Equal(HttpMethod.Post, replyMethod);
        Assert.Equal(NotesUri, replyUri);
        Assert.NotNull(replyBody);
        Assert.Contains("Fixed in the latest push.", Uri.UnescapeDataString(replyBody.Replace('+', ' ')), StringComparison.Ordinal);
        Assert.Equal("1126", noteId);
    }

    [Fact]
    public async Task ReplyAsync_WhenForbidden_NamesTheScopeAndRoleRequired()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var thread = CreateThread(host);
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);

        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri == UserUri
                ? GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" })
                : new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("{\"message\":\"403 Forbidden\"}"),
                });

        var sut = new GitLabReviewThreadReplyPublisher(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ReplyAsync(clientId, thread, "Fixed in the latest push."));

        Assert.Contains("api scope", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Guest or above", exception.Message, StringComparison.Ordinal);
        Assert.Contains("403 Forbidden", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplyAsync_WhenNotFound_SaysTheThreadMayBeGoneOrUnreadable()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var thread = CreateThread(host);
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);

        // GitLab answers a resource the caller may not access with 404, so the message has to name the
        // permission reading as well as the deleted-thread one.
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri == UserUri
                ? GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" })
                : new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{\"message\":\"404 Not found\"}"),
                });

        var sut = new GitLabReviewThreadReplyPublisher(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ReplyAsync(clientId, thread, "Fixed in the latest push."));

        Assert.Contains(DiscussionId, exception.Message, StringComparison.Ordinal);
        Assert.Contains("could not find", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("api scope", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplyAsync_WhenAcceptedWithoutANote_IsNotReportedAsPosted()
    {
        var clientId = Guid.NewGuid();
        var host = new ProviderHostRef(ScmProvider.GitLab, "https://gitlab.example.com");
        var thread = CreateThread(host);
        var connectionRepository = GitLabTestHelpers.CreateConnectionRepository(clientId, host);

        // A refusal wearing a success shape: the status says the call was accepted, the payload carries no
        // note, so nothing was actually written into the thread.
        var httpClientFactory = GitLabTestHelpers.CreateHttpClientFactory(request =>
            request.RequestUri!.AbsoluteUri == UserUri
                ? GitLabTestHelpers.CreateJsonResponse(new { username = "meister-dev" })
                : GitLabTestHelpers.CreateJsonResponse(new { message = "202 Accepted" }, HttpStatusCode.Accepted));

        var sut = new GitLabReviewThreadReplyPublisher(
            new GitLabConnectionVerifier(connectionRepository, httpClientFactory),
            httpClientFactory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.ReplyAsync(clientId, thread, "Fixed in the latest push."));

        Assert.Contains("returned no note", exception.Message, StringComparison.Ordinal);
        Assert.Contains("202 Accepted", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatReplyText_NeutralizesMarkupWithoutManglingQuotedCode()
    {
        const string input = "Use \"--no-verify\" only after removing <script>alert('xss')</script>.";

        var reply = GitLabReviewThreadReplyPublisher.FormatReplyText(input);

        Assert.Contains("\"--no-verify\"", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("&quot;", reply, StringComparison.Ordinal);
        Assert.Equal(-1, reply.IndexOf("<script>", StringComparison.Ordinal));
    }

    private static ReviewThreadRef CreateThread(ProviderHostRef host)
    {
        var repository = new RepositoryRef(host, "101", "acme/platform", "acme/platform/propr");
        var review = new CodeReviewRef(repository, CodeReviewPlatformKind.PullRequest, "4201", 42);
        return new ReviewThreadRef(review, DiscussionId, "src/file.ts", 18, isReviewerOwned: true);
    }
}
